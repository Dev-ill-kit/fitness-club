namespace WebApi.Contracts;

public class CreateEnrollmentRequest
{
    public long? ClassId { get; set; }

    public int? Seats { get; set; }
}

public class ChangeSeatsRequest
{
    public int? Seats { get; set; }
}

public class EnrollmentCreatedResponse
{
    public long Id { get; set; }

    public long ClassId { get; set; }

    public int Seats { get; set; }

    public string Status { get; set; } = "";

    public DateTime CreatedAt { get; set; }
}

public class CancelEnrollmentResponse
{
    public long Id { get; set; }

    public string Status { get; set; } = "";

    public int RefundedVisits { get; set; }
}

public class RoomResponse
{
    public long Id { get; set; }

    public string Name { get; set; } = "";
}

public class ClassResponse
{
    public long Id { get; set; }

    public string Title { get; set; } = "";

    public DateTime StartsAt { get; set; }

    public int DurationMinutes { get; set; }

    public bool IsCancelled { get; set; }

    public RoomResponse Room { get; set; } = null!;
}

public class PassResponse
{
    public int RemainingVisits { get; set; }

    public DateOnly ValidUntil { get; set; }
}

public class EnrollmentCardResponse
{
    public long Id { get; set; }

    public string Status { get; set; } = "";

    public int Seats { get; set; }

    public DateTime CreatedAt { get; set; }

    public ClassResponse Class { get; set; } = null!;

    public PassResponse Pass { get; set; } = null!;
}

public class WaitlistCreatedResponse
{
    public long Id { get; set; }

    public long ClassId { get; set; }

    public int Seats { get; set; }

    public int Position { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class WaitlistCancelledResponse
{
    public long Id { get; set; }

    public string Status { get; set; } = "";
}

public class ScheduleItemResponse
{
    public long ClassId { get; set; }

    public string Title { get; set; } = "";

    public DateTime StartsAt { get; set; }

    public int DurationMinutes { get; set; }

    public RoomResponse Room { get; set; } = null!;

    public int Capacity { get; set; }

    public int FreeSeats { get; set; }

    public int MySeats { get; set; }
}

public class ScheduleResponse
{
    public List<ScheduleItemResponse> Items { get; set; } = [];

    public string? ContinuationToken { get; set; }
}

public class LoadItemResponse
{
    public DateOnly Date { get; set; }

    public RoomResponse Room { get; set; } = null!;

    public int ClassesCount { get; set; }

    public int TotalCapacity { get; set; }

    public int BookedSeats { get; set; }
}

public class LoadResponse
{
    public List<LoadItemResponse> Items { get; set; } = [];
}
