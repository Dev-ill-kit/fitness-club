using Common.Caching;
using StackExchange.Redis;
using WebApi.Contracts;

namespace WebApi.Bookings;

public class CardCache
{
    private readonly ICacheService _cache;

    public CardCache(ICacheService cache)
    {
        _cache = cache;
    }

    public async Task<EnrollmentCardResponse?> TryGetAsync(long userId, long enrollmentId, long version, CancellationToken ct)
    {
        try
        {
            return await _cache.GetAsync<EnrollmentCardResponse>(Key(userId, enrollmentId, version), ct);
        }
        catch (RedisException)
        {
            // решение: если Redis на чтении недоступен, карточку беру из базы.
            return null;
        }
    }

    public async Task TrySetAsync(long userId, long enrollmentId, long version, EnrollmentCardResponse card, CancellationToken ct)
    {
        try
        {
            await _cache.SetAsync(Key(userId, enrollmentId, version), card, TimeSpan.FromMinutes(5), ct);
        }
        catch (RedisException)
        {
            // решение: карточка уже собрана из базы, запись в Redis можно пропустить.
        }
    }

    private static string Key(long userId, long enrollmentId, long version)
    {
        return $"card:{userId}:{enrollmentId}:v{version}";
    }
}
