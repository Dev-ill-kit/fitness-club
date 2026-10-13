using DataAccessLayer;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts;

namespace WebApi.Bookings;

public class ScheduleService
{
    private readonly ClubDbContext _db;

    public ScheduleService(ClubDbContext db)
    {
        _db = db;
    }

    public async Task<ScheduleResponse> GetAsync(
        long userId,
        DateTime from,
        DateTime to,
        long? roomId,
        int limit,
        DateTime? tokenStarts,
        long? tokenId,
        CancellationToken ct)
    {
        var query = _db.Classes
            .AsNoTracking()
            .Where(x => !x.IsCancelled && x.StartsAt >= from && x.StartsAt < to);

        // решение: отменённые отсекаю здесь, общим фильтром на занятие это не делаю.
        // решение: при одинаковом startsAt сортирую по id, иначе страница теряет строки.
        if (roomId != null)
        {
            query = query.Where(x => x.RoomId == roomId.Value);
        }

        if (tokenStarts != null && tokenId != null)
        {
            var starts = tokenStarts.Value;
            var id = tokenId.Value;
            query = query.Where(x => x.StartsAt > starts || (x.StartsAt == starts && x.Id > id));
        }

        var rows = await query
            .OrderBy(x => x.StartsAt)
            .ThenBy(x => x.Id)
            .Take(limit + 1)
            .Select(x => new ScheduleRow
            {
                ClassId = x.Id,
                Title = x.Title,
                StartsAt = x.StartsAt,
                DurationMinutes = x.DurationMinutes,
                RoomId = x.Room.Id,
                RoomName = x.Room.Name,
                Capacity = x.Capacity,
                Taken = x.Enrollments.Where(e => e.Status == BookingStatus.Active).Sum(e => (int?)e.Seats) ?? 0,
                MySeats = x.Enrollments.Where(e => e.Status == BookingStatus.Active && e.UserId == userId).Sum(e => (int?)e.Seats) ?? 0
            })
            .ToListAsync(ct);

        var hasMore = rows.Count > limit;

        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = rows.Select(x => new ScheduleItemResponse
        {
            ClassId = x.ClassId,
            Title = x.Title,
            StartsAt = ClockWork.AsUtc(x.StartsAt),
            DurationMinutes = x.DurationMinutes,
            Room = new RoomResponse { Id = x.RoomId, Name = x.RoomName },
            Capacity = x.Capacity,
            FreeSeats = Math.Max(0, x.Capacity - x.Taken),
            MySeats = x.MySeats
        }).ToList();

        string? token = null;

        if (hasMore && items.Count > 0)
        {
            var last = items[^1];
            token = ContinuationToken.Write(last.StartsAt, last.ClassId);
        }

        return new ScheduleResponse
        {
            Items = items,
            ContinuationToken = token
        };
    }

    private class ScheduleRow
    {
        public long ClassId { get; set; }

        public string Title { get; set; } = "";

        public DateTime StartsAt { get; set; }

        public int DurationMinutes { get; set; }

        public long RoomId { get; set; }

        public string RoomName { get; set; } = "";

        public int Capacity { get; set; }

        public int Taken { get; set; }

        public int MySeats { get; set; }
    }
}
