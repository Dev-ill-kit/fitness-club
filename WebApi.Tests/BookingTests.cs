using System.Net;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class BookingTests : ApiTestBase
{
    [TestMethod]
    public async Task LoginHealthAndSeedStayCompatible()
    {
        using var health = await SendAsync(HttpMethod.Get, "/health");
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        var token = await TokenAsync();
        Assert.IsFalse(string.IsNullOrWhiteSpace(token));
        var users = await InDbAsync(db => db.Users.CountAsync());
        var rooms = await InDbAsync(db => db.Rooms.CountAsync());
        Assert.AreEqual(205, users);
        Assert.AreEqual(10, rooms);
    }

    [TestMethod]
    public async Task EnrollSpendsVisitsAndCardMatchesCreatedAt()
    {
        var userId = await UserIdAsync("user001@club.test");
        await SetPassAsync(userId, 8, Today().AddDays(30));
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(3), 10);
        var token = await TokenAsync();
        using var created = await EnrollAsync(token, classId, 2, "enroll-card");
        var body = await ReadAsync(created);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, body.ToString());
        Assert.AreEqual("active", body.GetProperty("status").GetString());
        Assert.AreEqual(2, body.GetProperty("seats").GetInt32());
        Assert.AreEqual(6, await VisitsAsync(userId));

        using var card = await SendAsync(HttpMethod.Get, $"/enrollments/{body.GetProperty("id").GetInt64()}", token: token);
        var cardBody = await ReadAsync(card);
        Assert.AreEqual(HttpStatusCode.OK, card.StatusCode, cardBody.ToString());
        Assert.AreEqual(body.GetProperty("createdAt").GetDateTime(), cardBody.GetProperty("createdAt").GetDateTime());
        Assert.AreEqual(6, cardBody.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        Assert.IsFalse(cardBody.GetProperty("class").GetProperty("isCancelled").GetBoolean());
    }

    [TestMethod]
    public async Task IdempotencyReplayDoesNotCreateAnotherEnrollment()
    {
        var userId = await UserIdAsync("user002@club.test");
        await SetPassAsync(userId, 5, Today().AddDays(10));
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(2), 6);
        var token = await TokenAsync("user002@club.test");
        using var first = await EnrollAsync(token, classId, 1, "same-key");
        using var second = await EnrollAsync(token, classId, 1, "same-key");
        var firstBody = await ReadAsync(first);
        var secondBody = await ReadAsync(second);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, firstBody.ToString());
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode, secondBody.ToString());
        Assert.AreEqual(firstBody.GetProperty("id").GetInt64(), secondBody.GetProperty("id").GetInt64());
        Assert.AreEqual(firstBody.GetProperty("createdAt").GetString(), secondBody.GetProperty("createdAt").GetString());
        Assert.AreEqual(4, await VisitsAsync(userId));
        Assert.AreEqual(1, await ActiveSeatsAsync(classId));
    }

    [TestMethod]
    public async Task SameIdempotencyKeyWithAnotherBodyConflicts()
    {
        var userId = await UserIdAsync("user003@club.test");
        await SetPassAsync(userId, 5, Today().AddDays(10));
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(2), 6);
        var token = await TokenAsync("user003@club.test");
        using var first = await EnrollAsync(token, classId, 1, "conflict-key");
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        using var second = await EnrollAsync(token, classId, 2, "conflict-key");
        var body = await ReadAsync(second);
        Assert.AreEqual(HttpStatusCode.Conflict, second.StatusCode, body.ToString());
        Assert.AreEqual("idempotency-mismatch", Reason(body));
        Assert.AreEqual(4, await VisitsAsync(userId));
    }

    [TestMethod]
    public async Task MissingOrBlankIdempotencyKeyIsRejected()
    {
        var token = await TokenAsync();
        using var missing = await SendAsync(HttpMethod.Post, "/enrollments", """{"classId":1,"seats":1}""", token);
        using var blank = await EnrollAsync(token, 1, 1, "   ");
        Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    [TestMethod]
    public async Task InvalidSeatsAndUnknownClass()
    {
        var token = await TokenAsync();
        using var zero = await EnrollAsync(token, 1, 0, "zero");
        using var missing = await SendAsync(HttpMethod.Post, "/enrollments", """{"classId":1}""", token, "missing-seats");
        using var absent = await EnrollAsync(token, 999999, 1, "missing-class");
        using var path = await SendAsync(HttpMethod.Get, "/enrollments/abc", token: token);
        Assert.AreEqual(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, path.StatusCode);
    }

    [TestMethod]
    public async Task CancelledClassIsConflictNotMissing()
    {
        var userId = await UserIdAsync("user004@club.test");
        await SetPassAsync(userId, 4, Today().AddDays(5));
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(1), 8, cancelled: true);
        var token = await TokenAsync("user004@club.test");
        using var response = await EnrollAsync(token, classId, 1, "cancelled-class");
        var body = await ReadAsync(response);
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, body.ToString());
        Assert.AreEqual("class-cancelled", Reason(body));
        Assert.AreEqual(4, await VisitsAsync(userId));
    }

    [TestMethod]
    public async Task StartedExpiredAndExhaustedPassesDoNotChangeState()
    {
        var userId = await UserIdAsync("user005@club.test");
        var token = await TokenAsync("user005@club.test");
        var started = await InsertClassAsync(TestClock.Shared.UtcNow, 8);
        await SetPassAsync(userId, 4, Today().AddDays(5));
        using var startedResponse = await EnrollAsync(token, started, 1, "started");
        Assert.AreEqual("class-started", Reason(await ReadAsync(startedResponse)));

        var future = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(4), 8);
        await SetPassAsync(userId, 4, Today().AddDays(-1));
        using var expired = await EnrollAsync(token, future, 1, "expired");
        Assert.AreEqual("pass-expired", Reason(await ReadAsync(expired)));

        await SetPassAsync(userId, 1, Today().AddDays(5));
        using var exhausted = await EnrollAsync(token, future, 2, "exhausted");
        Assert.AreEqual("pass-exhausted", Reason(await ReadAsync(exhausted)));
        Assert.AreEqual(0, await ActiveSeatsAsync(future));
        Assert.AreEqual(1, await VisitsAsync(userId));
    }

    [TestMethod]
    public async Task CapacityRefusalLeavesVisitsUntouched()
    {
        var first = await UserIdAsync("user006@club.test");
        var second = await UserIdAsync("user007@club.test");
        await SetPassAsync(first, 3, Today().AddDays(5));
        await SetPassAsync(second, 3, Today().AddDays(5));
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(1), 1);
        var tokenA = await TokenAsync("user006@club.test");
        var tokenB = await TokenAsync("user007@club.test");
        using var ok = await EnrollAsync(tokenA, classId, 1, "cap-a");
        Assert.AreEqual(HttpStatusCode.Created, ok.StatusCode);
        using var denied = await EnrollAsync(tokenB, classId, 1, "cap-b");
        Assert.AreEqual("application/problem+json", denied.Content.Headers.ContentType!.MediaType);
        Assert.AreEqual("capacity-exceeded", Reason(await ReadAsync(denied)));
        Assert.AreEqual(3, await VisitsAsync(second));
        Assert.AreEqual(1, await ActiveSeatsAsync(classId));
    }

    [TestMethod]
    public async Task AnotherUserCannotReadOrCancelEnrollment()
    {
        var owner = await UserIdAsync("user008@club.test");
        await SetPassAsync(owner, 3, Today().AddDays(5));
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(1), 4);
        var ownerToken = await TokenAsync("user008@club.test");
        var otherToken = await TokenAsync("user009@club.test");
        using var created = await EnrollAsync(ownerToken, classId, 1, "private");
        var id = (await ReadAsync(created)).GetProperty("id").GetInt64();
        using var card = await SendAsync(HttpMethod.Get, $"/enrollments/{id}", token: otherToken);
        using var cancel = await SendAsync(HttpMethod.Post, $"/enrollments/{id}/cancel", token: otherToken);
        using var anonymous = await SendAsync(HttpMethod.Get, $"/enrollments/{id}");
        Assert.AreEqual(HttpStatusCode.NotFound, card.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, cancel.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [TestMethod]
    public async Task ChangeSeatsUpdatesPassAndSameCountDoesNot()
    {
        var userId = await UserIdAsync("user010@club.test");
        await SetPassAsync(userId, 6, Today().AddDays(20));
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(2), 10);
        var token = await TokenAsync("user010@club.test");
        using var created = await EnrollAsync(token, classId, 2, "seats-create");
        var id = (await ReadAsync(created)).GetProperty("id").GetInt64();
        using var increased = await SendAsync(HttpMethod.Post, $"/enrollments/{id}/seats", """{"seats":4}""", token);
        var increasedBody = await ReadAsync(increased);
        Assert.AreEqual(HttpStatusCode.OK, increased.StatusCode, increasedBody.ToString());
        Assert.AreEqual(4, increasedBody.GetProperty("seats").GetInt32());
        Assert.AreEqual(2, increasedBody.GetProperty("pass").GetProperty("remainingVisits").GetInt32());

        using var same = await SendAsync(HttpMethod.Post, $"/enrollments/{id}/seats", """{"seats":4}""", token);
        Assert.AreEqual(HttpStatusCode.OK, same.StatusCode);
        Assert.AreEqual(2, await VisitsAsync(userId));

        await SetPassAsync(userId, 0, Today().AddDays(-2));
        using var decreased = await SendAsync(HttpMethod.Post, $"/enrollments/{id}/seats", """{"seats":1}""", token);
        var decreasedBody = await ReadAsync(decreased);
        Assert.AreEqual(HttpStatusCode.OK, decreased.StatusCode, decreasedBody.ToString());
        Assert.AreEqual(3, await VisitsAsync(userId));

        using var card = await SendAsync(HttpMethod.Get, $"/enrollments/{id}", token: token);
        var cardBody = await ReadAsync(card);
        Assert.AreEqual(1, cardBody.GetProperty("seats").GetInt32());
        Assert.AreEqual(3, cardBody.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
    }

    [TestMethod]
    public async Task CannotChangeCancelledStartedOrClubCancelledEnrollment()
    {
        var userId = await UserIdAsync("user011@club.test");
        await SetPassAsync(userId, 6, Today().AddDays(10));
        var token = await TokenAsync("user011@club.test");
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddHours(5), 8);
        using var created = await EnrollAsync(token, classId, 1, "mutable");
        var id = (await ReadAsync(created)).GetProperty("id").GetInt64();
        using var cancel = await SendAsync(HttpMethod.Post, $"/enrollments/{id}/cancel", token: token);
        Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode);
        using var afterCancel = await SendAsync(HttpMethod.Post, $"/enrollments/{id}/seats", """{"seats":2}""", token);
        Assert.AreEqual("enrollment-cancelled", Reason(await ReadAsync(afterCancel)));

        var startedClass = await InsertClassAsync(TestClock.Shared.UtcNow.AddHours(5), 8);
        using var startedEnrollment = await EnrollAsync(token, startedClass, 1, "started-change");
        var startedId = (await ReadAsync(startedEnrollment)).GetProperty("id").GetInt64();
        await InDbAsync(async db =>
        {
            var scheduled = await db.Classes.SingleAsync(x => x.Id == startedClass);
            scheduled.StartsAt = TestClock.Shared.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        });
        using var startedChange = await SendAsync(HttpMethod.Post, $"/enrollments/{startedId}/seats", """{"seats":2}""", token);
        Assert.AreEqual("class-started", Reason(await ReadAsync(startedChange)));

        var clubClass = await InsertClassAsync(TestClock.Shared.UtcNow.AddHours(5), 8);
        using var clubEnrollment = await EnrollAsync(token, clubClass, 1, "club-change");
        var clubId = (await ReadAsync(clubEnrollment)).GetProperty("id").GetInt64();
        await InDbAsync(async db =>
        {
            var scheduled = await db.Classes.SingleAsync(x => x.Id == clubClass);
            scheduled.IsCancelled = true;
            await db.SaveChangesAsync();
        });
        using var clubChange = await SendAsync(HttpMethod.Post, $"/enrollments/{clubId}/seats", """{"seats":2}""", token);
        Assert.AreEqual("class-cancelled", Reason(await ReadAsync(clubChange)));
        using var clubCard = await SendAsync(HttpMethod.Get, $"/enrollments/{clubId}", token: token);
        var clubCardBody = await ReadAsync(clubCard);
        Assert.AreEqual(HttpStatusCode.OK, clubCard.StatusCode, clubCardBody.ToString());
        Assert.IsTrue(clubCardBody.GetProperty("class").GetProperty("isCancelled").GetBoolean());
    }

    [TestMethod]
    public async Task CancelRefundDependsOnTwoHoursAndRepeatStaysStable()
    {
        var userId = await UserIdAsync("user012@club.test");
        await SetPassAsync(userId, 5, Today().AddDays(10));
        var token = await TokenAsync("user012@club.test");
        var fullClass = await InsertClassAsync(TestClock.Shared.UtcNow.AddHours(2), 5);
        using var fullCreated = await EnrollAsync(token, fullClass, 2, "refund-full");
        var fullId = (await ReadAsync(fullCreated)).GetProperty("id").GetInt64();
        using var fullCancel = await SendAsync(HttpMethod.Post, $"/enrollments/{fullId}/cancel", token: token);
        var fullBody = await ReadAsync(fullCancel);
        Assert.AreEqual(2, fullBody.GetProperty("refundedVisits").GetInt32());
        Assert.AreEqual(5, await VisitsAsync(userId));
        TestClock.Shared.UtcNow = TestClock.Shared.UtcNow.AddDays(1);
        using var repeat = await SendAsync(HttpMethod.Post, $"/enrollments/{fullId}/cancel", token: token);
        var repeatBody = await ReadAsync(repeat);
        Assert.AreEqual(HttpStatusCode.OK, repeat.StatusCode);
        Assert.AreEqual(2, repeatBody.GetProperty("refundedVisits").GetInt32());
        Assert.AreEqual(5, await VisitsAsync(userId));

        TestClock.Shared.UtcNow = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        await SetPassAsync(userId, 5, Today().AddDays(10));
        var partialClass = await InsertClassAsync(TestClock.Shared.UtcNow.AddHours(2).AddSeconds(-1), 5);
        using var partialCreated = await EnrollAsync(token, partialClass, 2, "refund-none");
        var partialId = (await ReadAsync(partialCreated)).GetProperty("id").GetInt64();
        using var partialCancel = await SendAsync(HttpMethod.Post, $"/enrollments/{partialId}/cancel", token: token);
        var partialBody = await ReadAsync(partialCancel);
        Assert.AreEqual(0, partialBody.GetProperty("refundedVisits").GetInt32());
        Assert.AreEqual(3, await VisitsAsync(userId));
        Assert.AreEqual(0, await ActiveSeatsAsync(partialClass));
    }

    [TestMethod]
    public async Task ClubCancelledClassRefundsAfterStart()
    {
        var userId = await UserIdAsync("user013@club.test");
        await SetPassAsync(userId, 4, Today().AddDays(3));
        var token = await TokenAsync("user013@club.test");
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddHours(4), 4);
        using var created = await EnrollAsync(token, classId, 2, "club-cancel");
        var id = (await ReadAsync(created)).GetProperty("id").GetInt64();
        await InDbAsync(async db =>
        {
            var scheduled = await db.Classes.SingleAsync(x => x.Id == classId);
            scheduled.IsCancelled = true;
            scheduled.StartsAt = TestClock.Shared.UtcNow.AddHours(-3);
            await db.SaveChangesAsync();
        });
        using var cancel = await SendAsync(HttpMethod.Post, $"/enrollments/{id}/cancel", token: token);
        var body = await ReadAsync(cancel);
        Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode, body.ToString());
        Assert.AreEqual(2, body.GetProperty("refundedVisits").GetInt32());
        Assert.AreEqual(4, await VisitsAsync(userId));
    }

    [TestMethod]
    public async Task ParallelEnrollsKeepASingleSeat()
    {
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(1), 1);
        var tasks = new List<Task<HttpResponseMessage>>();

        for (var i = 21; i <= 35; i++)
        {
            var email = $"user{i:D3}@club.test";
            var userId = await UserIdAsync(email);
            await SetPassAsync(userId, 2, Today().AddDays(10));
            var token = await TokenAsync(email);
            tasks.Add(EnrollAsync(token, classId, 1, $"parallel-{i}"));
        }

        var responses = await Task.WhenAll(tasks);
        var codes = new List<HttpStatusCode>();

        foreach (var response in responses)
        {
            codes.Add(response.StatusCode);
            response.Dispose();
        }

        Assert.AreEqual(1, codes.Count(x => x == HttpStatusCode.Created));
        Assert.IsTrue(codes.All(x => x is HttpStatusCode.Created or HttpStatusCode.Conflict));
        Assert.AreEqual(1, await ActiveSeatsAsync(classId));
    }
}
