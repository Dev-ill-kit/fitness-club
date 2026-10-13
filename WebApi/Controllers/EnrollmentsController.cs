using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Bookings;
using WebApi.Contracts;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[RateLimited]
[Route("enrollments")]
public class EnrollmentsController : BaseApiController
{
    private readonly BookingService _bookings;

    public EnrollmentsController(BookingService bookings)
    {
        _bookings = bookings;
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateEnrollmentRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TrySeats(request?.ClassId, request?.Seats, out var classId, out var seats))
        {
            return ApiResults.Invalid();
        }

        var key = NormalizeKey(idempotencyKey);

        if (key == null)
        {
            return ApiResults.Invalid();
        }

        return ApiResults.From(await _bookings.CreateAsync(CurrentUserId, classId, seats, key, ct));
    }

    [HttpPost("{id}/seats")]
    public async Task<IActionResult> ChangeSeats(string id, [FromBody] ChangeSeatsRequest? request, CancellationToken ct)
    {
        if (!TryId(id, out var enrollmentId) || request?.Seats is not int seats || seats < 1)
        {
            return ApiResults.Invalid();
        }

        return ApiResults.From(await _bookings.ChangeSeatsAsync(CurrentUserId, enrollmentId, seats, ct));
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(string id, CancellationToken ct)
    {
        if (!TryId(id, out var enrollmentId))
        {
            return ApiResults.Invalid();
        }

        return ApiResults.From(await _bookings.CancelAsync(CurrentUserId, enrollmentId, ct));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (!TryId(id, out var enrollmentId))
        {
            return ApiResults.Invalid();
        }

        return ApiResults.From(await _bookings.GetCardAsync(CurrentUserId, enrollmentId, ct));
    }

    [HttpPost("waitlist")]
    public async Task<IActionResult> Join(
        [FromBody] CreateEnrollmentRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TrySeats(request?.ClassId, request?.Seats, out var classId, out var seats))
        {
            return ApiResults.Invalid();
        }

        var key = NormalizeKey(idempotencyKey);

        if (key == null)
        {
            return ApiResults.Invalid();
        }

        return ApiResults.From(await _bookings.JoinWaitlistAsync(CurrentUserId, classId, seats, key, ct));
    }

    [HttpPost("waitlist/{id}/cancel")]
    public async Task<IActionResult> Leave(string id, CancellationToken ct)
    {
        if (!TryId(id, out var entryId))
        {
            return ApiResults.Invalid();
        }

        return ApiResults.From(await _bookings.LeaveWaitlistAsync(CurrentUserId, entryId, ct));
    }

    private static string? NormalizeKey(string? key)
    {
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    private static bool TryId(string value, out long id)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    private static bool TrySeats(long? classId, int? seats, out long parsedClassId, out int parsedSeats)
    {
        parsedClassId = classId ?? 0;
        parsedSeats = seats ?? 0;
        return classId != null && seats is >= 1;
    }
}
