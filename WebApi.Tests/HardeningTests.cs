using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class HardeningTests : ApiTestBase
{
    [TestMethod]
    public async Task ParallelWaitlistWithTheSameKeyStoresOneEntry()
    {
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(3), 1);
        var holderId = await UserIdAsync("user090@club.test");
        var waiterId = await UserIdAsync("user091@club.test");
        await SetPassAsync(holderId, 2, Today().AddDays(5));
        await SetPassAsync(waiterId, 2, Today().AddDays(5));
        var holder = await TokenAsync("user090@club.test");
        var waiter = await TokenAsync("user091@club.test");
        using var enrolled = await EnrollAsync(holder, classId, 1, "hold-same-key");
        Assert.AreEqual(HttpStatusCode.Created, enrolled.StatusCode);
        var tasks = Enumerable.Range(0, 6).Select(_ => SendAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            $$"""{"classId":{{classId}},"seats":1}""",
            waiter,
            "same-wait-key")).ToArray();
        var responses = await Task.WhenAll(tasks);
        var ids = new List<long>();

        foreach (var response in responses)
        {
            var body = await ReadAsync(response);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, body.ToString());
            ids.Add(body.GetProperty("id").GetInt64());
            response.Dispose();
        }

        Assert.AreEqual(1, ids.Distinct().Count());
        var waiting = await InDbAsync(db => db.WaitlistEntries.CountAsync(x => x.ClassId == classId && x.Status == "waiting"));
        Assert.AreEqual(1, waiting);
    }

    [TestMethod]
    public async Task ParallelEnrollWithTheSameKeySpendsVisitsOnce()
    {
        var userId = await UserIdAsync("user092@club.test");
        await SetPassAsync(userId, 6, Today().AddDays(5));
        var token = await TokenAsync("user092@club.test");
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(3), 8);
        var tasks = Enumerable.Range(0, 6).Select(_ => EnrollAsync(token, classId, 1, "same-enroll-key")).ToArray();
        var responses = await Task.WhenAll(tasks);
        var ids = new List<long>();

        foreach (var response in responses)
        {
            var body = await ReadAsync(response);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, body.ToString());
            ids.Add(body.GetProperty("id").GetInt64());
            response.Dispose();
        }

        Assert.AreEqual(1, ids.Distinct().Count());
        Assert.AreEqual(5, await VisitsAsync(userId));
        Assert.AreEqual(1, await ActiveSeatsAsync(classId));
    }

    [TestMethod]
    public async Task IdempotencyKeyDoesNotCrossUsersOrOperations()
    {
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(2), 4);
        var firstId = await UserIdAsync("user093@club.test");
        var secondId = await UserIdAsync("user094@club.test");
        await SetPassAsync(firstId, 4, Today().AddDays(5));
        await SetPassAsync(secondId, 4, Today().AddDays(5));
        var first = await TokenAsync("user093@club.test");
        var second = await TokenAsync("user094@club.test");
        using var left = await EnrollAsync(first, classId, 1, "shared-key");
        using var right = await EnrollAsync(second, classId, 1, "shared-key");
        var leftBody = await ReadAsync(left);
        var rightBody = await ReadAsync(right);
        Assert.AreEqual(HttpStatusCode.Created, left.StatusCode, leftBody.ToString());
        Assert.AreEqual(HttpStatusCode.Created, right.StatusCode, rightBody.ToString());
        Assert.AreNotEqual(leftBody.GetProperty("id").GetInt64(), rightBody.GetProperty("id").GetInt64());

        var fullClass = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(2), 1);
        using var held = await EnrollAsync(first, fullClass, 1, "both");
        Assert.AreEqual(HttpStatusCode.Created, held.StatusCode);
        using var queued = await SendAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            $$"""{"classId":{{fullClass}},"seats":1}""",
            second,
            "both");
        var queuedBody = await ReadAsync(queued);
        Assert.AreEqual(HttpStatusCode.Created, queued.StatusCode, queuedBody.ToString());
        Assert.AreEqual(1, queuedBody.GetProperty("position").GetInt32());
    }

    [TestMethod]
    public async Task StoredEnrollResponseSurvivesLaterClassCancellation()
    {
        var userId = await UserIdAsync("user095@club.test");
        await SetPassAsync(userId, 3, Today().AddDays(5));
        var token = await TokenAsync("user095@club.test");
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(2), 4);
        using var created = await EnrollAsync(token, classId, 1, "frozen-enroll");
        var createdBody = await ReadAsync(created);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        await InDbAsync(async db =>
        {
            var scheduled = await db.Classes.SingleAsync(x => x.Id == classId);
            scheduled.IsCancelled = true;
            await db.SaveChangesAsync();
        });
        using var replay = await EnrollAsync(token, classId, 1, "frozen-enroll");
        var replayBody = await ReadAsync(replay);
        Assert.AreEqual(HttpStatusCode.Created, replay.StatusCode, replayBody.ToString());
        Assert.AreEqual(createdBody.GetProperty("id").GetInt64(), replayBody.GetProperty("id").GetInt64());
        Assert.AreEqual(1, await ActiveSeatsAsync(classId));
        Assert.AreEqual(2, await VisitsAsync(userId));
    }

    [TestMethod]
    public async Task ExpiredPassCanWaitAndFulfilledEntryIsGone()
    {
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddHours(6), 1);
        var holderId = await UserIdAsync("user096@club.test");
        var waiterId = await UserIdAsync("user097@club.test");
        await SetPassAsync(holderId, 2, Today().AddDays(5));
        await SetPassAsync(waiterId, 0, Today().AddDays(-3));
        var holder = await TokenAsync("user096@club.test");
        var waiter = await TokenAsync("user097@club.test");
        using var enrolled = await EnrollAsync(holder, classId, 1, "expired-hold");
        var enrollmentId = (await ReadAsync(enrolled)).GetProperty("id").GetInt64();
        using var joined = await SendAsync(
            HttpMethod.Post,
            "/enrollments/waitlist",
            $$"""{"classId":{{classId}},"seats":1}""",
            waiter,
            "expired-can-wait");
        var joinedBody = await ReadAsync(joined);
        Assert.AreEqual(HttpStatusCode.Created, joined.StatusCode, joinedBody.ToString());
        await SetPassAsync(waiterId, 2, Today().AddDays(5));
        using var cancel = await SendAsync(HttpMethod.Post, $"/enrollments/{enrollmentId}/cancel", token: holder);
        Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode, (await ReadAsync(cancel)).ToString());
        Assert.AreEqual(1, await VisitsAsync(waiterId));
        using var leave = await SendAsync(
            HttpMethod.Post,
            $"/enrollments/waitlist/{joinedBody.GetProperty("id").GetInt64()}/cancel",
            token: waiter);
        Assert.AreEqual(HttpStatusCode.NotFound, leave.StatusCode);
    }

    [TestMethod]
    public async Task LeavingAndJoiningAgainCreatesANewQueueEntry()
    {
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(2), 1);
        var holderId = await UserIdAsync("user098@club.test");
        var waiterId = await UserIdAsync("user099@club.test");
        await SetPassAsync(holderId, 2, Today().AddDays(5));
        await SetPassAsync(waiterId, 2, Today().AddDays(5));
        var holder = await TokenAsync("user098@club.test");
        var waiter = await TokenAsync("user099@club.test");
        using var enrolled = await EnrollAsync(holder, classId, 1, "rejoin-hold");
        Assert.AreEqual(HttpStatusCode.Created, enrolled.StatusCode);
        using var first = await SendAsync(HttpMethod.Post, "/enrollments/waitlist", $$"""{"classId":{{classId}},"seats":1}""", waiter, "rejoin-1");
        var firstId = (await ReadAsync(first)).GetProperty("id").GetInt64();
        using var leave = await SendAsync(HttpMethod.Post, $"/enrollments/waitlist/{firstId}/cancel", token: waiter);
        Assert.AreEqual(HttpStatusCode.OK, leave.StatusCode);
        using var second = await SendAsync(HttpMethod.Post, "/enrollments/waitlist", $$"""{"classId":{{classId}},"seats":1}""", waiter, "rejoin-2");
        var secondBody = await ReadAsync(second);
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode, secondBody.ToString());
        Assert.AreNotEqual(firstId, secondBody.GetProperty("id").GetInt64());
        Assert.AreEqual(1, secondBody.GetProperty("position").GetInt32());
    }

    [TestMethod]
    public async Task CardCacheShowsVisitsAfterALaterBooking()
    {
        var userId = await UserIdAsync("user100@club.test");
        await SetPassAsync(userId, 6, Today().AddDays(10));
        var token = await TokenAsync("user100@club.test");
        var firstClass = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(2), 4);
        var secondClass = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(3), 4);
        using var created = await EnrollAsync(token, firstClass, 1, "cache-first");
        var id = (await ReadAsync(created)).GetProperty("id").GetInt64();
        using var cached = await SendAsync(HttpMethod.Get, $"/enrollments/{id}", token: token);
        Assert.AreEqual(5, (await ReadAsync(cached)).GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        using var other = await EnrollAsync(token, secondClass, 2, "cache-second");
        Assert.AreEqual(HttpStatusCode.Created, other.StatusCode);
        using var fresh = await SendAsync(HttpMethod.Get, $"/enrollments/{id}", token: token);
        var body = await ReadAsync(fresh);
        Assert.AreEqual(3, body.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        Assert.AreEqual(1, body.GetProperty("seats").GetInt32());

        for (var i = 0; i < 20; i++)
        {
            using var again = await SendAsync(HttpMethod.Get, $"/enrollments/{id}", token: token);
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);
        }
    }

    [TestMethod]
    public async Task ScheduleSumsEveryActiveBookingAndNeverShowsNegativeFreeSeats()
    {
        var userId = await UserIdAsync("user101@club.test");
        await SetPassAsync(userId, 6, Today().AddDays(5));
        var token = await TokenAsync("user101@club.test");
        var classId = await InsertClassAsync(new DateTime(2036, 1, 4, 9, 0, 0, DateTimeKind.Utc), 3);
        using var first = await EnrollAsync(token, classId, 1, "mine-1");
        using var second = await EnrollAsync(token, classId, 1, "mine-2");
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
        var enrollmentId = (await ReadAsync(second)).GetProperty("id").GetInt64();
        await InDbAsync(async db =>
        {
            var enrollment = await db.Enrollments.SingleAsync(x => x.Id == enrollmentId);
            enrollment.Seats = 10;
            await db.SaveChangesAsync();
        });
        using var page = await SendAsync(
            HttpMethod.Get,
            "/schedule?from=2036-01-04T00:00:00&to=2036-01-05T00:00:00",
            token: token);
        var item = (await ReadAsync(page)).GetProperty("items").EnumerateArray().Single(x => x.GetProperty("classId").GetInt64() == classId);
        Assert.AreEqual(11, item.GetProperty("mySeats").GetInt32());
        Assert.AreEqual(0, item.GetProperty("freeSeats").GetInt32());
    }

    [TestMethod]
    public async Task PassRemainsValidOnItsLastDay()
    {
        var userId = await UserIdAsync("user102@club.test");
        await SetPassAsync(userId, 2, Today());
        var token = await TokenAsync("user102@club.test");
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(20), 4);
        using var response = await EnrollAsync(token, classId, 1, "last-day");
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, (await ReadAsync(response)).ToString());
        Assert.AreEqual(1, await VisitsAsync(userId));
    }

    [TestMethod]
    public async Task ExtraJsonFieldsDoNotChangeTheBooking()
    {
        var userId = await UserIdAsync("user103@club.test");
        await SetPassAsync(userId, 3, Today().AddDays(5));
        var token = await TokenAsync("user103@club.test");
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(2), 4);
        using var response = await SendAsync(
            HttpMethod.Post,
            "/enrollments",
            $$"""{"classId":{{classId}},"seats":1,"status":"cancelled","userId":1}""",
            token,
            "ignore-extra");
        var body = await ReadAsync(response);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, body.ToString());
        Assert.AreEqual("active", body.GetProperty("status").GetString());
        Assert.IsFalse(body.TryGetProperty("userId", out _));
    }

    [TestMethod]
    public async Task ForbiddenResponsesConsumeTheSameQuota()
    {
        var token = await TokenAsync("user104@club.test");

        for (var i = 0; i < 60; i++)
        {
            using var denied = await SendAsync(HttpMethod.Get, "/analytics/load?from=2032-01-01&to=2032-01-01", token: token);
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode, $"request {i + 1}");
        }

        using var limited = await SendAsync(HttpMethod.Get, "/schedule?from=2032-01-01T00:00:00&to=2032-01-02T00:00:00&limit=1", token: token);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.IsFalse((await ReadAsync(limited)).TryGetProperty("reason", out _));
    }

    [TestMethod]
    public async Task AnonymousRequestsStayUnauthorizedPastTheLimit()
    {
        for (var i = 0; i < 61; i++)
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                "/schedule?from=2033-01-01T00:00:00&to=2033-01-02T00:00:00&limit=1",
                token: "not-a-token");
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, $"request {i + 1}");
        }
    }

    [TestMethod]
    public async Task HistoricalSeedCancellationsDidNotRefundVisits()
    {
        var userId = await UserIdAsync("user199@club.test");
        var nonzero = await InDbAsync(db => db.Enrollments.CountAsync(x => x.UserId == userId && x.RefundedVisits != 0));
        Assert.AreEqual(0, nonzero);
    }

    [TestMethod]
    public async Task InvalidRequestHasNoReasonField()
    {
        var token = await TokenAsync("user105@club.test");
        using var response = await SendAsync(HttpMethod.Post, "/enrollments/abc/cancel", token: token);
        var body = await ReadAsync(response);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.AreEqual("Invalid request", body.GetProperty("title").GetString());
        Assert.IsFalse(body.TryGetProperty("reason", out _));
    }
}
