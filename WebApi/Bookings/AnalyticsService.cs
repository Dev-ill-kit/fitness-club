using DataAccessLayer;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts;

namespace WebApi.Bookings;

public class LoadRow
{
    public DateTime Date { get; set; }

    public long RoomId { get; set; }

    public string RoomName { get; set; } = "";

    public int ClassesCount { get; set; }

    public int TotalCapacity { get; set; }

    public int BookedSeats { get; set; }
}

public class AnalyticsService
{
    private readonly ClubDbContext _db;

    public AnalyticsService(ClubDbContext db)
    {
        _db = db;
    }

    public async Task<LoadResponse> GetAsync(DateOnly from, DateOnly to, long? roomId, CancellationToken ct)
    {
        // решение: границы суток беру по starts_at, так условие попадает в индекс.
        var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toExclusive = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var sql = """
            SELECT (c.starts_at AT TIME ZONE 'UTC')::date AS "Date",
                   r.id AS "RoomId",
                   r.name AS "RoomName",
                   COUNT(*)::int AS "ClassesCount",
                   COALESCE(SUM(c.capacity), 0)::int AS "TotalCapacity",
                   COALESCE(SUM(b.seats), 0)::int AS "BookedSeats"
            FROM classes c
            JOIN rooms r ON r.id = c.room_id
            LEFT JOIN LATERAL (
                SELECT COALESCE(SUM(e.seats), 0)::int AS seats
                FROM enrollments e
                WHERE e.class_id = c.id AND e.status = 'active'
            ) b ON true
            WHERE c.is_cancelled = false
              AND c.starts_at >= {0}
              AND c.starts_at < {1}
            """;

        if (roomId != null)
        {
            sql += " AND c.room_id = {2}";
        }

        sql += """
             GROUP BY (c.starts_at AT TIME ZONE 'UTC')::date, r.id, r.name
             ORDER BY (c.starts_at AT TIME ZONE 'UTC')::date, r.id
            """;

        var rows = roomId is null
            ? await _db.Database.SqlQueryRaw<LoadRow>(sql, fromUtc, toExclusive).ToListAsync(ct)
            : await _db.Database.SqlQueryRaw<LoadRow>(sql, fromUtc, toExclusive, roomId.Value).ToListAsync(ct);

        return new LoadResponse
        {
            Items = rows.Select(x => new LoadItemResponse
            {
                Date = DateOnly.FromDateTime(x.Date),
                Room = new RoomResponse { Id = x.RoomId, Name = x.RoomName },
                ClassesCount = x.ClassesCount,
                TotalCapacity = x.TotalCapacity,
                BookedSeats = x.BookedSeats
            }).ToList()
        };
    }
}
