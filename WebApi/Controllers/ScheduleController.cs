using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Bookings;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[RateLimited]
[Route("schedule")]
public class ScheduleController : BaseApiController
{
    private readonly ScheduleService _schedule;

    public ScheduleController(ScheduleService schedule)
    {
        _schedule = schedule;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!RequestTimes.TryInstant(Request.Query["from"], out var from)
            || !RequestTimes.TryInstant(Request.Query["to"], out var to)
            || to <= from)
        {
            return ApiResults.Invalid();
        }

        long? roomId = null;

        if (Request.Query.ContainsKey("roomId"))
        {
            if (!long.TryParse(Request.Query["roomId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRoom))
            {
                return ApiResults.Invalid();
            }

            roomId = parsedRoom;
        }

        var limit = 20;

        if (Request.Query.ContainsKey("limit"))
        {
            if (!int.TryParse(Request.Query["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out limit)
                || limit < 1
                || limit > 100)
            {
                return ApiResults.Invalid();
            }
        }

        DateTime? tokenStarts = null;
        long? tokenId = null;
        var token = Request.Query["continuationToken"].ToString();

        if (!string.IsNullOrWhiteSpace(token))
        {
            if (!ContinuationToken.TryRead(token, out var starts, out var id))
            {
                return ApiResults.Invalid();
            }

            tokenStarts = starts;
            tokenId = id;
        }

        var body = await _schedule.GetAsync(CurrentUserId, from, to, roomId, limit, tokenStarts, tokenId, ct);
        return Ok(body);
    }
}
