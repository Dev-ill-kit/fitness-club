using System.Globalization;
using System.Text.RegularExpressions;

namespace WebApi.Bookings;

public static partial class RequestTimes
{
    private static readonly string[] PlainFormats =
    [
        "yyyy-MM-dd",
        "yyyy'-'MM'-'dd'T'HH':'mm",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'FFFFFFF"
    ];

    private static readonly string[] ZonedFormats =
    [
        "yyyy'-'MM'-'dd'T'HH':'mmK",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ssK",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'FFFFFFFK"
    ];

    public static bool TryInstant(string? text, out DateTime utc)
    {
        utc = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();

        if (text.Length > 10 && text[10] == 't' && text[4] == '-' && text[7] == '-')
        {
            text = text.Substring(0, 10) + "T" + text.Substring(11);
        }

        if (ZonePattern().IsMatch(text))
        {
            if (!DateTimeOffset.TryParseExact(text, ZonedFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset))
            {
                return false;
            }

            utc = offset.UtcDateTime;
            return true;
        }

        if (!DateTime.TryParseExact(text, PlainFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }

    public static bool TryDate(string? text, out DateOnly date)
    {
        date = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();

        if (text.Length != 10)
        {
            return false;
        }

        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    [GeneratedRegex(@"(?:[zZ]|[+-]\d{2}:\d{2}|[+-]\d{4})$", RegexOptions.CultureInvariant)]
    private static partial Regex ZonePattern();
}
