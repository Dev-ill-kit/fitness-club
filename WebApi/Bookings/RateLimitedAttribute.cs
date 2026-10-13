namespace WebApi.Bookings;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RateLimitedAttribute : Attribute;
