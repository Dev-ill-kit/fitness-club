using WebApi.Bookings;

namespace WebApi.Tests;

public class TestClock : IClock
{
    public static readonly TestClock Shared = new();

    public DateTime UtcNow { get; set; } = DateTime.UtcNow;
}
