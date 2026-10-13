using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Bookings;

public class BookingResult
{
    public int StatusCode { get; init; }

    public string? Json { get; init; }

    public object? Body { get; init; }

    public string? Title { get; init; }

    public string? Reason { get; init; }

    public static BookingResult Stored(int statusCode, string json)
    {
        return new BookingResult { StatusCode = statusCode, Json = json };
    }

    public static BookingResult Ok(int statusCode, object body)
    {
        return new BookingResult { StatusCode = statusCode, Body = body };
    }

    public static BookingResult Fail(int statusCode, string title, string? reason = null)
    {
        return new BookingResult { StatusCode = statusCode, Title = title, Reason = reason };
    }
}

public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, Options);
    }
}

public static class ApiResults
{
    public static IActionResult From(BookingResult result)
    {
        if (result.Json != null)
        {
            return new ContentResult
            {
                StatusCode = result.StatusCode,
                Content = result.Json,
                ContentType = "application/json"
            };
        }

        if (result.StatusCode < 400)
        {
            return new ObjectResult(result.Body) { StatusCode = result.StatusCode };
        }

        return Problem(result.StatusCode, result.Title ?? "Error", result.Reason);
    }

    public static IActionResult Invalid()
    {
        return Problem(StatusCodes.Status400BadRequest, "Invalid request");
    }

    public static IActionResult Problem(int statusCode, string title, string? reason = null)
    {
        var details = new ProblemDetails
        {
            Status = statusCode,
            Title = title
        };

        if (reason != null)
        {
            details.Extensions["reason"] = reason;
        }

        var result = new ObjectResult(details) { StatusCode = statusCode };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }
}
