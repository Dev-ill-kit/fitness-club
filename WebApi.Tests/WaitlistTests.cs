using System.Net;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class WaitlistTests : ApiTestBase
{
    [TestMethod]
    public async Task JoinRequiresAFullClassAndKeepsStoredPosition()
    {
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(2), 2);
        var holder = await UserIdAsync("user040@club.test");
        var first = await UserIdAsync("user041@club.test");
        var second = await UserIdAsync("user042@club.test");
        await SetPassAsync(holder, 4, Today().AddDays(10));
        await SetPassAsync(first, 4, Today().AddDays(10));
        await SetPassAsync(second, 4, Today().AddDays(10));
        var holderToken = await TokenAsync("user040@club.test");
        var firstToken = await TokenAsync("user041@club.test");
        var secondToken = await TokenAsync("user042@club.test");
        using var open = await SendAsync(HttpMethod.Post, "/enrollments/waitlist", $$"""{"classId":{{classId}},"seats":2}""", firstToken, "too-early");
        Assert.AreEqual("seats-available", Reason(await ReadAsync(open)));
        using var enrolled = await EnrollAsync(holderToken, classId, 2, "fill-class");
        Assert.AreEqual(HttpStatusCode.Created, enrolled.StatusCode);
        using var joined = await SendAsync(HttpMethod.Post, "/enrollments/waitlist", $$"""{"classId":{{classId}},"seats":1}""", firstToken, "wait-1");
        var joinedBody = await ReadAsync(joined);
        Assert.AreEqual(HttpStatusCode.Created, joined.StatusCode, joinedBody.ToString());
        Assert.AreEqual(1, joinedBody.GetProperty("position").GetInt32());
        using var again = await SendAsync(HttpMethod.Post, "/enrollments/waitlist", $$"""{"classId":{{classId}},"seats":1}""", firstToken, "wait-again");
        Assert.AreEqual("already-waiting", Reason(await ReadAsync(again)));
        using var secondJoin = await SendAsync(HttpMethod.Post, "/enrollments/waitlist", $$"""{"classId":{{classId}},"seats":1}""", secondToken, "wait-2");
        Assert.AreEqual(2, (await ReadAsync(secondJoin)).GetProperty("position").GetInt32());
        using var leave = await SendAsync(HttpMethod.Post, $"/enrollments/waitlist/{joinedBody.GetProperty("id").GetInt64()}/cancel", token: firstToken);
        Assert.AreEqual(HttpStatusCode.OK, leave.StatusCode);
        using var replay = await SendAsync(HttpMethod.Post, "/enrollments/waitlist", $$"""{"classId":{{classId}},"seats":1}""", firstToken, "wait-1");
        var replayBody = await ReadAsync(replay);
        Assert.AreEqual(1, replayBody.GetProperty("position").GetInt32());
        using var leaveAgain = await SendAsync(HttpMethod.Post, $"/enrollments/waitlist/{joinedBody.GetProperty("id").GetInt64()}/cancel", token: firstToken);
        Assert.AreEqual(HttpStatusCode.OK, leaveAgain.StatusCode);
        using var stranger = await SendAsync(HttpMethod.Post, $"/enrollments/waitlist/{joinedBody.GetProperty("id").GetInt64()}/cancel", token: secondToken);
        Assert.AreEqual(HttpStatusCode.NotFound, stranger.StatusCode);
    }

    [TestMethod]
    public async Task CancelPromotesTheNextEligibleWaiter()
    {
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddHours(3), 1);
        var holderId = await UserIdAsync("user043@club.test");
        var waiterId = await UserIdAsync("user044@club.test");
        await SetPassAsync(holderId, 3, Today().AddDays(5));
        await SetPassAsync(waiterId, 3, Today().AddDays(5));
        var holder = await TokenAsync("user043@club.test");
        var waiter = await TokenAsync("user044@club.test");
        using var enrolled = await EnrollAsync(holder, classId, 1, "promote-hold");
        var enrollmentId = (await ReadAsync(enrolled)).GetProperty("id").GetInt64();
        using var waiting = await SendAsync(HttpMethod.Post, "/enrollments/waitlist", $$"""{"classId":{{classId}},"seats":1}""", waiter, "promote-wait");
        Assert.AreEqual(HttpStatusCode.Created, waiting.StatusCode, (await ReadAsync(waiting)).ToString());
        using var cancel = await SendAsync(HttpMethod.Post, $"/enrollments/{enrollmentId}/cancel", token: holder);
        Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode, (await ReadAsync(cancel)).ToString());
        Assert.AreEqual(1, await ActiveSeatsAsync(classId));
        Assert.AreEqual(2, await VisitsAsync(waiterId));
        Assert.AreEqual(3, await VisitsAsync(holderId));
        var fulfilled = await InDbAsync(db => db.WaitlistEntries.CountAsync(x => x.ClassId == classId && x.Status == "fulfilled"));
        Assert.AreEqual(1, fulfilled);
    }

    [TestMethod]
    public async Task QueueStopsWhenTheHeadNeedsMoreSeats()
    {
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddHours(4), 2);
        var holderId = await UserIdAsync("user045@club.test");
        var headId = await UserIdAsync("user046@club.test");
        var tailId = await UserIdAsync("user047@club.test");
        await SetPassAsync(holderId, 4, Today().AddDays(5));
        await SetPassAsync(headId, 4, Today().AddDays(5));
        await SetPassAsync(tailId, 4, Today().AddDays(5));
        var holder = await TokenAsync("user045@club.test");
        var head = await TokenAsync("user046@club.test");
        var tail = await TokenAsync("user047@club.test");
        using var enrolled = await EnrollAsync(holder, classId, 2, "block-hold");
        var enrollmentId = (await ReadAsync(enrolled)).GetProperty("id").GetInt64();
        using var headJoin = await SendAsync(HttpMethod.Post, "/enrollments/waitlist", $$"""{"classId":{{classId}},"seats":3}""", head, "block-head");
        using var tailJoin = await SendAsync(HttpMethod.Post, "/enrollments/waitlist", $$"""{"classId":{{classId}},"seats":1}""", tail, "block-tail");
        Assert.AreEqual(HttpStatusCode.Created, headJoin.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, tailJoin.StatusCode);
        using var cancel = await SendAsync(HttpMethod.Post, $"/enrollments/{enrollmentId}/cancel", token: holder);
        Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode);
        Assert.AreEqual(0, await ActiveSeatsAsync(classId));
        Assert.AreEqual(4, await VisitsAsync(tailId));
        var waiting = await InDbAsync(db => db.WaitlistEntries.CountAsync(x => x.ClassId == classId && x.Status == "waiting"));
        Assert.AreEqual(2, waiting);
    }

    [TestMethod]
    public async Task IneligibleHeadDoesNotBlockTheNextWaiter()
    {
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddHours(4), 1);
        var holderId = await UserIdAsync("user048@club.test");
        var headId = await UserIdAsync("user049@club.test");
        var nextId = await UserIdAsync("user050@club.test");
        await SetPassAsync(holderId, 3, Today().AddDays(5));
        await SetPassAsync(headId, 3, Today().AddDays(5));
        await SetPassAsync(nextId, 3, Today().AddDays(5));
        var holder = await TokenAsync("user048@club.test");
        var head = await TokenAsync("user049@club.test");
        var next = await TokenAsync("user050@club.test");
        using var enrolled = await EnrollAsync(holder, classId, 1, "skip-hold");
        var enrollmentId = (await ReadAsync(enrolled)).GetProperty("id").GetInt64();
        using var headJoin = await SendAsync(HttpMethod.Post, "/enrollments/waitlist", $$"""{"classId":{{classId}},"seats":1}""", head, "skip-head");
        using var nextJoin = await SendAsync(HttpMethod.Post, "/enrollments/waitlist", $$"""{"classId":{{classId}},"seats":1}""", next, "skip-next");
        Assert.AreEqual(HttpStatusCode.Created, headJoin.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, nextJoin.StatusCode);
        await SetPassAsync(headId, 0, Today().AddDays(-1));
        using var cancel = await SendAsync(HttpMethod.Post, $"/enrollments/{enrollmentId}/cancel", token: holder);
        Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode, (await ReadAsync(cancel)).ToString());
        Assert.AreEqual(1, await ActiveSeatsAsync(classId));
        Assert.AreEqual(2, await VisitsAsync(nextId));
        Assert.AreEqual(0, await VisitsAsync(headId));
    }

    [TestMethod]
    public async Task SmallerBookingDoesNotMoveTheQueue()
    {
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(1), 2);
        var holderId = await UserIdAsync("user051@club.test");
        var waiterId = await UserIdAsync("user052@club.test");
        await SetPassAsync(holderId, 4, Today().AddDays(5));
        await SetPassAsync(waiterId, 4, Today().AddDays(5));
        var holder = await TokenAsync("user051@club.test");
        var waiter = await TokenAsync("user052@club.test");
        using var enrolled = await EnrollAsync(holder, classId, 2, "shrink-hold");
        var enrollmentId = (await ReadAsync(enrolled)).GetProperty("id").GetInt64();
        using var waiting = await SendAsync(HttpMethod.Post, "/enrollments/waitlist", $$"""{"classId":{{classId}},"seats":1}""", waiter, "shrink-wait");
        Assert.AreEqual(HttpStatusCode.Created, waiting.StatusCode);
        using var shrink = await SendAsync(HttpMethod.Post, $"/enrollments/{enrollmentId}/seats", """{"seats":1}""", holder);
        Assert.AreEqual(HttpStatusCode.OK, shrink.StatusCode);
        Assert.AreEqual(1, await ActiveSeatsAsync(classId));
        Assert.AreEqual(4, await VisitsAsync(waiterId));
        var stillWaiting = await InDbAsync(db => db.WaitlistEntries.CountAsync(x => x.ClassId == classId && x.Status == "waiting"));
        Assert.AreEqual(1, stillWaiting);
    }

    [TestMethod]
    public async Task DirectEnrollmentTakesAFreeSeatWhileSomeoneWaits()
    {
        var classId = await InsertClassAsync(TestClock.Shared.UtcNow.AddDays(1), 2);
        var holderId = await UserIdAsync("user053@club.test");
        var walkerId = await UserIdAsync("user054@club.test");
        await SetPassAsync(holderId, 4, Today().AddDays(5));
        await SetPassAsync(walkerId, 4, Today().AddDays(5));
        var holder = await TokenAsync("user053@club.test");
        var walker = await TokenAsync("user054@club.test");
        var guest = await TokenAsync("user055@club.test");
        await SetPassAsync(await UserIdAsync("user055@club.test"), 4, Today().AddDays(5));
        using var enrolled = await EnrollAsync(holder, classId, 1, "cut-hold");
        Assert.AreEqual(HttpStatusCode.Created, enrolled.StatusCode);
        using var waiting = await SendAsync(HttpMethod.Post, "/enrollments/waitlist", $$"""{"classId":{{classId}},"seats":2}""", walker, "cut-wait");
        Assert.AreEqual(HttpStatusCode.Created, waiting.StatusCode, (await ReadAsync(waiting)).ToString());
        using var cut = await EnrollAsync(guest, classId, 1, "cut-in");
        Assert.AreEqual(HttpStatusCode.Created, cut.StatusCode, (await ReadAsync(cut)).ToString());
        Assert.AreEqual(2, await ActiveSeatsAsync(classId));
        Assert.AreEqual(4, await VisitsAsync(walkerId));
    }
}
