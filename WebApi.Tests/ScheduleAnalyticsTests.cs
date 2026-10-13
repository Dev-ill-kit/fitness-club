using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class ScheduleAnalyticsTests : ApiTestBase
{
    [TestMethod]
    public async Task SchedulePagesStableTiesAndSkipsCancelledClasses()
    {
        var token = await TokenAsync("user060@club.test");
        var userId = await UserIdAsync("user060@club.test");
        await SetPassAsync(userId, 6, Today().AddDays(30));
        var start = new DateTime(2035, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        var first = await InsertClassAsync(start, 5);
        var second = await InsertClassAsync(start, 5);
        var cancelled = await InsertClassAsync(start.AddHours(1), 5, cancelled: true);
        using var enrolled = await EnrollAsync(token, first, 2, "schedule-mine");
        Assert.AreEqual(HttpStatusCode.Created, enrolled.StatusCode);
        using var page = await SendAsync(
            HttpMethod.Get,
            "/schedule?from=2035-03-01T00:00:00&to=2035-03-02T00:00:00&limit=1",
            token: token);
        var pageBody = await ReadAsync(page);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, pageBody.ToString());
        Assert.AreEqual(1, pageBody.GetProperty("items").GetArrayLength());
        var continuation = pageBody.GetProperty("continuationToken").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(continuation));
        using var next = await SendAsync(
            HttpMethod.Get,
            $"/schedule?from=2035-03-01T00:00:00&to=2035-03-02T00:00:00&limit=1&continuationToken={Uri.EscapeDataString(continuation!)}",
            token: token);
        var nextBody = await ReadAsync(next);
        Assert.AreEqual(1, nextBody.GetProperty("items").GetArrayLength());
        var seen = new[]
        {
            pageBody.GetProperty("items")[0].GetProperty("classId").GetInt64(),
            nextBody.GetProperty("items")[0].GetProperty("classId").GetInt64()
        };
        CollectionAssert.AreEquivalent(new[] { first, second }, seen);
        Assert.IsFalse(seen.Contains(cancelled));
        var mine = pageBody.GetProperty("items")[0].GetProperty("classId").GetInt64() == first
            ? pageBody.GetProperty("items")[0]
            : nextBody.GetProperty("items")[0];
        Assert.AreEqual(2, mine.GetProperty("mySeats").GetInt32());
        Assert.AreEqual(3, mine.GetProperty("freeSeats").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, nextBody.GetProperty("continuationToken").ValueKind);
        using var badToken = await SendAsync(HttpMethod.Get, "/schedule?from=2035-03-01T00:00:00&to=2035-03-02T00:00:00&continuationToken=%%%", token: token);
        using var badLimit = await SendAsync(HttpMethod.Get, "/schedule?from=2035-03-01T00:00:00&to=2035-03-02T00:00:00&limit=0", token: token);
        using var equalRange = await SendAsync(HttpMethod.Get, "/schedule?from=2035-03-01T00:00:00&to=2035-03-01T00:00:00", token: token);
        using var injectedRoom = await SendAsync(HttpMethod.Get, "/schedule?from=2035-03-01T00:00:00&to=2035-03-02T00:00:00&roomId=1%20OR%201=1", token: token);
        using var emptyRoom = await SendAsync(HttpMethod.Get, "/schedule?from=2035-03-01T00:00:00&to=2035-03-02T00:00:00&roomId=999999", token: token);
        Assert.AreEqual(HttpStatusCode.BadRequest, badToken.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, badLimit.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, equalRange.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, injectedRoom.StatusCode);
        Assert.AreEqual(0, (await ReadAsync(emptyRoom)).GetProperty("items").GetArrayLength());
    }

    [TestMethod]
    public async Task AnalyticsCountsUtcDaysAndHidesCancelledData()
    {
        var admin = await TokenAsync("admin001@club.test");
        var user = await TokenAsync("user061@club.test");
        var userId = await UserIdAsync("user061@club.test");
        await SetPassAsync(userId, 4, Today().AddDays(10));
        var day = new DateTime(2035, 4, 2, 23, 30, 0, DateTimeKind.Utc);
        var classId = await InsertClassAsync(day, 8);
        await InsertClassAsync(day.AddMinutes(10), 8, cancelled: true);
        using var enrolled = await EnrollAsync(user, classId, 3, "analytics-book");
        var enrollmentId = (await ReadAsync(enrolled)).GetProperty("id").GetInt64();
        using var denied = await SendAsync(HttpMethod.Get, "/analytics/load?from=2035-04-02&to=2035-04-02", token: user);
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using var load = await SendAsync(HttpMethod.Get, "/analytics/load?from=2035-04-02&to=2035-04-02", token: admin);
        var body = await ReadAsync(load);
        Assert.AreEqual(HttpStatusCode.OK, load.StatusCode, body.ToString());
        var roomId = await RoomOf(classId);
        var row = body.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("room").GetProperty("id").GetInt64() == roomId);
        Assert.AreEqual(1, row.GetProperty("classesCount").GetInt32());
        Assert.AreEqual(8, row.GetProperty("totalCapacity").GetInt32());
        Assert.AreEqual(3, row.GetProperty("bookedSeats").GetInt32());
        Assert.AreEqual("2035-04-02", row.GetProperty("date").GetString());
        using var timed = await SendAsync(HttpMethod.Get, "/analytics/load?from=2035-04-02T00:00:00Z&to=2035-04-02", token: admin);
        using var backwards = await SendAsync(HttpMethod.Get, "/analytics/load?from=2035-04-03&to=2035-04-02", token: admin);
        Assert.AreEqual(HttpStatusCode.BadRequest, timed.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, backwards.StatusCode);
        using var cancel = await SendAsync(HttpMethod.Post, $"/enrollments/{enrollmentId}/cancel", token: user);
        Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode);
        using var after = await SendAsync(HttpMethod.Get, "/analytics/load?from=2035-04-02&to=2035-04-02&roomId=" + row.GetProperty("room").GetProperty("id").GetInt64(), token: admin);
        var afterBody = await ReadAsync(after);
        var afterRow = afterBody.GetProperty("items").EnumerateArray().Single();
        Assert.AreEqual(0, afterRow.GetProperty("bookedSeats").GetInt32());
        Assert.AreEqual(1, afterRow.GetProperty("classesCount").GetInt32());
        using var gap = await SendAsync(HttpMethod.Get, "/analytics/load?from=2035-04-01&to=2035-04-03", token: admin);
        var gapItems = (await ReadAsync(gap)).GetProperty("items").EnumerateArray().Where(x => x.GetProperty("room").GetProperty("id").GetInt64() == roomId).ToList();
        Assert.AreEqual(1, gapItems.Count);
        using var unknownRoom = await SendAsync(HttpMethod.Get, "/analytics/load?from=2035-04-02&to=2035-04-02&roomId=999999", token: admin);
        Assert.AreEqual(0, (await ReadAsync(unknownRoom)).GetProperty("items").GetArrayLength());
        using var injected = await SendAsync(HttpMethod.Get, "/analytics/load?from=2035-04-02&to=2035-04-02&roomId=1%20OR%201=1", token: admin);
        Assert.AreEqual(HttpStatusCode.BadRequest, injected.StatusCode);
    }

    [TestMethod]
    public async Task SixtyFirstRequestIsRejected()
    {
        var token = await TokenAsync("user062@club.test");

        for (var i = 0; i < 60; i++)
        {
            using var response = await SendAsync(HttpMethod.Get, "/schedule?from=2031-01-01T00:00:00&to=2031-01-02T00:00:00&limit=1", token: token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"request {i + 1}");
        }

        using var limited = await SendAsync(HttpMethod.Get, "/analytics/load?from=2031-01-01&to=2031-01-01", token: token);
        var limitedBody = await ReadAsync(limited);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode, limitedBody.ToString());
        Assert.AreEqual("application/problem+json", limited.Content.Headers.ContentType!.MediaType);
        Assert.IsFalse(limitedBody.TryGetProperty("reason", out _));
    }

    [TestMethod]
    public async Task ScheduleAcceptsLowercaseTimeSeparator()
    {
        var token = await TokenAsync("user063@club.test");
        using var response = await SendAsync(
            HttpMethod.Get,
            "/schedule?from=2031-11-02t12:00:00Z&to=2031-11-02T13:00:00Z",
            token: token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<long> RoomOf(long classId)
    {
        return await InDbAsync(db => db.Classes.Where(x => x.Id == classId).Select(x => x.RoomId).SingleAsync());
    }
}
