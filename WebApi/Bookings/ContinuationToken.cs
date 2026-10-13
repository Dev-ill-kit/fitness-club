using System.Globalization;
using System.Text;

namespace WebApi.Bookings;

public static class ContinuationToken
{
    public static string Write(DateTime startsAt, long id)
    {
        var payload = $"{ClockWork.AsUtc(startsAt).Ticks.ToString(CultureInfo.InvariantCulture)}:{id.ToString(CultureInfo.InvariantCulture)}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    public static bool TryRead(string token, out DateTime startsAt, out long id)
    {
        startsAt = default;
        id = 0;

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            var parts = text.Split(':');

            if (parts.Length != 2
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out id))
            {
                return false;
            }

            startsAt = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
