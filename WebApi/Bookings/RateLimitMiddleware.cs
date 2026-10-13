using System.Security.Claims;
using StackExchange.Redis;

namespace WebApi.Bookings;

public class RateLimitMiddleware
{
    private const string Script = """
        local key = KEYS[1]
        local now = tonumber(ARGV[1])
        local window = tonumber(ARGV[2])
        local limit = tonumber(ARGV[3])
        local member = ARGV[4]
        redis.call('ZREMRANGEBYSCORE', key, '-inf', now - window)
        local count = redis.call('ZCARD', key)
        if count >= limit then
          redis.call('PEXPIRE', key, window)
          return count + 1
        end
        redis.call('ZADD', key, now, member)
        redis.call('PEXPIRE', key, window)
        return count + 1
        """;

    private readonly RequestDelegate _next;

    public RateLimitMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IConnectionMultiplexer redis)
    {
        var endpoint = context.GetEndpoint();

        if (endpoint?.Metadata.GetMetadata<RateLimitedAttribute>() == null)
        {
            await _next(context);
            return;
        }

        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userId))
        {
            await _next(context);
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var member = $"{now}:{Guid.NewGuid():N}";
        var database = redis.GetDatabase();
        var count = (long)await database.ScriptEvaluateAsync(
            Script,
            [$"rate:{userId}"],
            [now, 60_000, 60, member]);

        // решение: окно скользящее, 60 секунд, один счётчик на все восемь маршрутов.
        if (count > 60)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(
                new { status = StatusCodes.Status429TooManyRequests, title = "Too many requests" },
                ApiJson.Options,
                "application/problem+json",
                context.RequestAborted);
            return;
        }

        await _next(context);
    }
}
