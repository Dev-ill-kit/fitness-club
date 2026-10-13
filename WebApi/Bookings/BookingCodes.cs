namespace WebApi.Bookings;

public static class BookingStatus
{
    public const string Active = "active";

    public const string Cancelled = "cancelled";

    public const string Waiting = "waiting";

    public const string Fulfilled = "fulfilled";
}

public static class BookingReason
{
    public const string CapacityExceeded = "capacity-exceeded";

    public const string PassExhausted = "pass-exhausted";

    public const string PassExpired = "pass-expired";

    public const string ClassStarted = "class-started";

    public const string ClassCancelled = "class-cancelled";

    public const string EnrollmentCancelled = "enrollment-cancelled";

    public const string SeatsAvailable = "seats-available";

    public const string AlreadyWaiting = "already-waiting";

    public const string IdempotencyMismatch = "idempotency-mismatch";
}

public static class IdempotencyOperation
{
    public const string Enroll = "enroll";

    public const string Waitlist = "waitlist";
}
