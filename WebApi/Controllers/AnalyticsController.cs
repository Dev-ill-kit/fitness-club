using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Bookings;

namespace WebApi.Controllers;

[ApiController]
[Authorize(Roles = "admin")]
[RateLimited]
[Route("analytics")]
public class AnalyticsController : BaseApiController
{
    private readonly AnalyticsService _analytics;

    public AnalyticsController(AnalyticsService analytics)
    {
        _analytics = analytics;
    }

    [HttpGet("load")]
    public async Task<IActionResult> Load(CancellationToken ct)
    {
        if (!RequestTimes.TryDate(Request.Query["from"], out var from)
            || !RequestTimes.TryDate(Request.Query["to"], out var to)
            || to < from)
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

        try
        {
            var body = await _analytics.GetAsync(from, to, roomId, ct);
            return Ok(body);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ApiResults.Invalid();
        }
    }
}
