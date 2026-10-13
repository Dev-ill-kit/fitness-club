using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WebApi.Tests;

public abstract class ApiTestBase
{
    private static ApiApplicationFactory? _factory;
    private static HttpClient? _client;
    private static readonly Dictionary<string, string> Tokens = new();

    protected static HttpClient Client => _client!;

    [TestInitialize]
    public async Task ResetAsync()
    {
        _factory ??= new ApiApplicationFactory();
        _client ??= _factory.CreateClient();
        TestClock.Shared.UtcNow = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        await TestDatabase.ResetRedisAsync();
    }

    protected static async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        string? json = null,
        string? token = null,
        string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, url);

        if (json != null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (token != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (idempotencyKey != null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return await Client.SendAsync(request);
    }

    protected static async Task<string> TokenAsync(string email = "user001@club.test")
    {
        if (Tokens.TryGetValue(email, out var cached))
        {
            return cached;
        }

        using var response = await SendAsync(
            HttpMethod.Post,
            "/auth/login",
            $"{{\"email\":\"{email}\",\"password\":\"Password123!\"}}");
        var body = await ReadAsync(response);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body.ToString());
        var token = body.GetProperty("accessToken").GetString()!;
        Tokens[email] = token;
        return token;
    }

    protected static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    protected static string? Reason(JsonElement body)
    {
        return body.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
    }

    protected static async Task<T> InDbAsync<T>(Func<ClubDbContext, Task<T>> action)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        return await action(db);
    }

    protected static Task InDbAsync(Func<ClubDbContext, Task> action)
    {
        return InDbAsync(async db =>
        {
            await action(db);
            return 0;
        });
    }

    protected static async Task<long> UserIdAsync(string email)
    {
        return await InDbAsync(db => db.Users.Where(x => x.Email == email).Select(x => x.Id).SingleAsync());
    }

    protected static async Task<long> InsertClassAsync(DateTime startsAt, int capacity, bool cancelled = false)
    {
        return await InDbAsync(async db =>
        {
            var roomId = await db.Rooms.Select(x => x.Id).FirstAsync();
            var scheduled = new Class
            {
                RoomId = roomId,
                Title = "QA",
                StartsAt = startsAt,
                DurationMinutes = 60,
                Capacity = capacity,
                IsCancelled = cancelled
            };
            db.Classes.Add(scheduled);
            await db.SaveChangesAsync();
            return scheduled.Id;
        });
    }

    protected static async Task SetPassAsync(long userId, int visits, DateOnly validUntil)
    {
        await InDbAsync(async db =>
        {
            var pass = await db.Passes.SingleAsync(x => x.UserId == userId);
            pass.RemainingVisits = visits;
            pass.ValidUntil = validUntil;
            await db.SaveChangesAsync();
        });
    }

    protected static async Task<int> VisitsAsync(long userId)
    {
        return await InDbAsync(db => db.Passes.Where(x => x.UserId == userId).Select(x => x.RemainingVisits).SingleAsync());
    }

    protected static async Task<int> ActiveSeatsAsync(long classId)
    {
        return await InDbAsync(async db =>
            (int)(await db.Enrollments
                .Where(x => x.ClassId == classId && x.Status == "active")
                .SumAsync(x => (int?)x.Seats) ?? 0));
    }

    protected static async Task<HttpResponseMessage> EnrollAsync(
        string token,
        long classId,
        int seats,
        string key)
    {
        return await SendAsync(
            HttpMethod.Post,
            "/enrollments",
            $"{{\"classId\":{classId},\"seats\":{seats}}}",
            token,
            key);
    }

    protected static DateOnly Today()
    {
        return DateOnly.FromDateTime(TestClock.Shared.UtcNow);
    }
}
