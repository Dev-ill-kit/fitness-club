using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Contracts;

namespace WebApi.Bookings;

public class BookingService
{
    private readonly ClubDbContext _db;
    private readonly IClock _clock;
    private readonly CardCache _cache;

    public BookingService(ClubDbContext db, IClock clock, CardCache cache)
    {
        _db = db;
        _clock = clock;
        _cache = cache;
    }

    public async Task<BookingResult> CreateAsync(
        long userId,
        long classId,
        int seats,
        string idempotencyKey,
        CancellationToken ct)
    {
        var now = ClockWork.Truncate(_clock.UtcNow);
        var requestHash = RequestHash(classId, seats);
        var existing = await StoredAnswerAsync(userId, IdempotencyOperation.Enroll, idempotencyKey, requestHash, ct);

        if (existing != null)
        {
            return existing;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            if (!await DbLocks.TryLockClassAsync(_db, classId, ct))
            {
                return NotFound();
            }

            var raced = await StoredAnswerAsync(userId, IdempotencyOperation.Enroll, idempotencyKey, requestHash, ct);

            if (raced != null)
            {
                return raced;
            }

            var scheduled = await _db.Classes.FirstAsync(x => x.Id == classId, ct);
            var classError = RejectUnbookableClass(scheduled, now);

            if (classError != null)
            {
                return classError;
            }

            await DbLocks.LockPassAsync(_db, userId, ct);
            var pass = await _db.Passes.SingleAsync(x => x.UserId == userId, ct);
            var passError = RejectPassSpend(pass, seats, now);

            if (passError != null)
            {
                return passError;
            }

            var taken = await TakenSeatsAsync(classId, ct);

            if (scheduled.Capacity - taken < seats)
            {
                return Conflict("Capacity exceeded", BookingReason.CapacityExceeded);
            }

            // решение: свободное место может забрать обычная запись, очередь его не держит.
            var enrollment = new Enrollment
            {
                UserId = userId,
                ClassId = classId,
                Seats = seats,
                RefundedVisits = 0,
                Status = BookingStatus.Active,
                CreatedAt = now
            };

            _db.Enrollments.Add(enrollment);
            AddVisits(pass, -seats);
            await _db.SaveChangesAsync(ct);

            var json = ApiJson.Serialize(new EnrollmentCreatedResponse
            {
                Id = enrollment.Id,
                ClassId = classId,
                Seats = seats,
                Status = BookingStatus.Active,
                CreatedAt = now
            });

            AddIdempotency(userId, IdempotencyOperation.Enroll, idempotencyKey, requestHash, StatusCodes.Status201Created, json, now);
            await _db.SaveChangesAsync(ct);
            await BumpCardsAsync([userId], ct);
            await tx.CommitAsync(ct);
            return BookingResult.Stored(StatusCodes.Status201Created, json);
        }
        catch (DbUpdateException exception) when (IsConstraint(exception, "ux_idempotency_user_operation_key"))
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            var winner = await FindIdempotencyAsync(userId, IdempotencyOperation.Enroll, idempotencyKey, ct);

            if (winner == null)
            {
                throw;
            }

            return Replay(winner, idempotencyKey, requestHash);
        }
    }

    public async Task<BookingResult> ChangeSeatsAsync(long userId, long enrollmentId, int seats, CancellationToken ct)
    {
        var now = ClockWork.Truncate(_clock.UtcNow);
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var pointer = await _db.Enrollments
            .AsNoTracking()
            .Where(x => x.Id == enrollmentId && x.UserId == userId)
            .Select(x => new { x.ClassId })
            .FirstOrDefaultAsync(ct);

        if (pointer == null || !await DbLocks.TryLockClassAsync(_db, pointer.ClassId, ct))
        {
            return NotFound();
        }

        var enrollment = await _db.Enrollments
            .Include(x => x.Class)
            .ThenInclude(x => x.Room)
            .FirstAsync(x => x.Id == enrollmentId && x.UserId == userId, ct);
        var stateError = RejectMutableEnrollment(enrollment, now);

        if (stateError != null)
        {
            return stateError;
        }

        if (enrollment.Seats == seats)
        {
            var currentPass = await _db.Passes.AsNoTracking().SingleAsync(x => x.UserId == userId, ct);
            return BookingResult.Ok(StatusCodes.Status200OK, MapCard(enrollment, currentPass));
        }

        await DbLocks.LockPassAsync(_db, userId, ct);
        var pass = await _db.Passes.SingleAsync(x => x.UserId == userId, ct);
        var delta = seats - enrollment.Seats;

        if (delta > 0)
        {
            var passError = RejectPassSpend(pass, delta, now);

            if (passError != null)
            {
                return passError;
            }

            var taken = await TakenSeatsAsync(enrollment.ClassId, ct);

            if (enrollment.Class.Capacity - taken < delta)
            {
                return Conflict("Capacity exceeded", BookingReason.CapacityExceeded);
            }
        }

        // решение: на уменьшении мест очередь не двигаю, в задании это сказано только про отмену.
        enrollment.Seats = seats;
        AddVisits(pass, -delta);
        await _db.SaveChangesAsync(ct);
        await BumpCardsAsync([userId], ct);
        await tx.CommitAsync(ct);
        return BookingResult.Ok(StatusCodes.Status200OK, MapCard(enrollment, pass));
    }

    public async Task<BookingResult> CancelAsync(long userId, long enrollmentId, CancellationToken ct)
    {
        var now = ClockWork.Truncate(_clock.UtcNow);
        var pointer = await _db.Enrollments
            .AsNoTracking()
            .Where(x => x.Id == enrollmentId && x.UserId == userId)
            .Select(x => new { x.ClassId })
            .FirstOrDefaultAsync(ct);

        if (pointer == null)
        {
            return NotFound();
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        if (!await DbLocks.TryLockClassAsync(_db, pointer.ClassId, ct))
        {
            return NotFound();
        }

        var enrollment = await _db.Enrollments
            .Include(x => x.Class)
            .FirstAsync(x => x.Id == enrollmentId && x.UserId == userId, ct);

        if (enrollment.Status == BookingStatus.Cancelled)
        {
            return BookingResult.Ok(StatusCodes.Status200OK, CancelBody(enrollment));
        }

        var startsAt = ClockWork.AsUtc(enrollment.Class.StartsAt);

        if (!enrollment.Class.IsCancelled && now >= startsAt)
        {
            return Conflict("Class started", BookingReason.ClassStarted);
        }

        var waiterIds = enrollment.Class.IsCancelled
            ? []
            : await _db.WaitlistEntries
                .Where(x => x.ClassId == enrollment.ClassId && x.Status == BookingStatus.Waiting)
                .Select(x => x.UserId)
                .ToListAsync(ct);
        var lockIds = waiterIds.Append(userId).Distinct().Order().ToArray();

        foreach (var lockId in lockIds)
        {
            await DbLocks.LockPassAsync(_db, lockId, ct);
        }

        var passes = await _db.Passes
            .Where(x => lockIds.Contains(x.UserId))
            .ToDictionaryAsync(x => x.UserId, ct);
        var refund = enrollment.Class.IsCancelled || startsAt - now >= TimeSpan.FromHours(2)
            ? enrollment.Seats
            : 0;
        var released = enrollment.Seats;
        enrollment.Status = BookingStatus.Cancelled;
        enrollment.CancelledAt = now;
        enrollment.RefundedVisits = refund;
        AddVisits(passes[userId], refund);
        var touched = new List<long> { userId };

        if (!enrollment.Class.IsCancelled)
        {
            var taken = await TakenSeatsAsync(enrollment.ClassId, ct);
            var free = enrollment.Class.Capacity - taken + released;
            await PromoteAsync(enrollment.Class, now, free, passes, touched, ct);
        }

        await _db.SaveChangesAsync(ct);
        await BumpCardsAsync(touched, ct);
        await tx.CommitAsync(ct);
        return BookingResult.Ok(StatusCodes.Status200OK, CancelBody(enrollment));
    }

    public async Task<BookingResult> GetCardAsync(long userId, long enrollmentId, CancellationToken ct)
    {
        var version = await _db.Users.AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => x.CardVersion)
            .SingleAsync(ct);
        var cached = await _cache.TryGetAsync(userId, enrollmentId, version, ct);

        if (cached != null && await CachedClassMatchesAsync(cached, ct))
        {
            return BookingResult.Ok(StatusCodes.Status200OK, cached);
        }

        var enrollment = await _db.Enrollments
            .AsNoTracking()
            .Include(x => x.Class)
            .ThenInclude(x => x.Room)
            .FirstOrDefaultAsync(x => x.Id == enrollmentId && x.UserId == userId, ct);

        if (enrollment == null)
        {
            return NotFound();
        }

        var pass = await _db.Passes.AsNoTracking().SingleAsync(x => x.UserId == userId, ct);
        var card = MapCard(enrollment, pass);
        await _cache.TrySetAsync(userId, enrollmentId, version, card, ct);
        return BookingResult.Ok(StatusCodes.Status200OK, card);
    }

    public async Task<BookingResult> JoinWaitlistAsync(
        long userId,
        long classId,
        int seats,
        string idempotencyKey,
        CancellationToken ct)
    {
        var now = ClockWork.Truncate(_clock.UtcNow);
        var requestHash = RequestHash(classId, seats);
        var existing = await StoredAnswerAsync(userId, IdempotencyOperation.Waitlist, idempotencyKey, requestHash, ct);

        if (existing != null)
        {
            return existing;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            if (!await DbLocks.TryLockClassAsync(_db, classId, ct))
            {
                return NotFound();
            }

            var raced = await StoredAnswerAsync(userId, IdempotencyOperation.Waitlist, idempotencyKey, requestHash, ct);

            if (raced != null)
            {
                return raced;
            }

            var scheduled = await _db.Classes.FirstAsync(x => x.Id == classId, ct);
            var classError = RejectUnbookableClass(scheduled, now);

            if (classError != null)
            {
                return classError;
            }

            var taken = await TakenSeatsAsync(classId, ct);

            if (scheduled.Capacity - taken >= seats)
            {
                return Conflict("Seats available", BookingReason.SeatsAvailable);
            }

            var alreadyWaiting = await _db.WaitlistEntries.AnyAsync(
                x => x.ClassId == classId && x.UserId == userId && x.Status == BookingStatus.Waiting,
                ct);

            if (alreadyWaiting)
            {
                return Conflict("Already waiting", BookingReason.AlreadyWaiting);
            }

            // решение: в очередь пускаю без проверки абонемента, списываю при выдаче места.
            var entry = new WaitlistEntry
            {
                UserId = userId,
                ClassId = classId,
                Seats = seats,
                Status = BookingStatus.Waiting,
                CreatedAt = now
            };

            _db.WaitlistEntries.Add(entry);
            await _db.SaveChangesAsync(ct);
            var ahead = await _db.WaitlistEntries.CountAsync(
                x => x.ClassId == classId
                    && x.Status == BookingStatus.Waiting
                    && (x.CreatedAt < entry.CreatedAt || (x.CreatedAt == entry.CreatedAt && x.Id < entry.Id)),
                ct);
            var json = ApiJson.Serialize(new WaitlistCreatedResponse
            {
                Id = entry.Id,
                ClassId = classId,
                Seats = seats,
                Position = ahead + 1,
                CreatedAt = now
            });

            AddIdempotency(userId, IdempotencyOperation.Waitlist, idempotencyKey, requestHash, StatusCodes.Status201Created, json, now);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return BookingResult.Stored(StatusCodes.Status201Created, json);
        }
        catch (DbUpdateException exception) when (IsConstraint(exception, "ux_idempotency_user_operation_key"))
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            var winner = await FindIdempotencyAsync(userId, IdempotencyOperation.Waitlist, idempotencyKey, ct);

            if (winner == null)
            {
                throw;
            }

            return Replay(winner, idempotencyKey, requestHash);
        }
        catch (DbUpdateException exception) when (IsConstraint(exception, "ux_waitlist_active_user_class"))
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return Conflict("Already waiting", BookingReason.AlreadyWaiting);
        }
    }

    public async Task<BookingResult> LeaveWaitlistAsync(long userId, long entryId, CancellationToken ct)
    {
        var now = ClockWork.Truncate(_clock.UtcNow);
        var pointer = await _db.WaitlistEntries
            .AsNoTracking()
            .Where(x => x.Id == entryId && x.UserId == userId)
            .Select(x => new { x.ClassId, x.Status })
            .FirstOrDefaultAsync(ct);

        if (pointer == null || pointer.Status == BookingStatus.Fulfilled)
        {
            return NotFound();
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        if (!await DbLocks.TryLockClassAsync(_db, pointer.ClassId, ct))
        {
            return NotFound();
        }

        var entry = await _db.WaitlistEntries.FirstAsync(x => x.Id == entryId && x.UserId == userId, ct);

        // решение: место уже выдали, повторный уход из очереди для меня это 404.
        if (entry.Status == BookingStatus.Fulfilled)
        {
            return NotFound();
        }

        if (entry.Status == BookingStatus.Cancelled)
        {
            return BookingResult.Ok(StatusCodes.Status200OK, new WaitlistCancelledResponse
            {
                Id = entry.Id,
                Status = BookingStatus.Cancelled
            });
        }

        entry.Status = BookingStatus.Cancelled;
        entry.CancelledAt = now;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return BookingResult.Ok(StatusCodes.Status200OK, new WaitlistCancelledResponse
        {
            Id = entry.Id,
            Status = BookingStatus.Cancelled
        });
    }

    private async Task PromoteAsync(
        Class scheduled,
        DateTime now,
        long free,
        Dictionary<long, Pass> passes,
        List<long> touched,
        CancellationToken ct)
    {
        var waiters = await _db.WaitlistEntries
            .Where(x => x.ClassId == scheduled.Id && x.Status == BookingStatus.Waiting)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);

        foreach (var waiter in waiters)
        {
            var pass = passes[waiter.UserId];
            var passOk = DateOnly.FromDateTime(now) <= pass.ValidUntil && pass.RemainingVisits >= waiter.Seats;

            // решение: абонемент не годится - пропускаю, место могут взять следующие.
            if (!passOk)
            {
                continue;
            }

            // решение: первому подходящему мест мало - очередь стоит, следующие не обгоняют.
            if (free < waiter.Seats)
            {
                break;
            }

            AddVisits(pass, -waiter.Seats);
            free -= waiter.Seats;
            waiter.Status = BookingStatus.Fulfilled;
            touched.Add(waiter.UserId);
            _db.Enrollments.Add(new Enrollment
            {
                UserId = waiter.UserId,
                ClassId = scheduled.Id,
                Seats = waiter.Seats,
                RefundedVisits = 0,
                Status = BookingStatus.Active,
                CreatedAt = now
            });
        }
    }

    private async Task<bool> CachedClassMatchesAsync(EnrollmentCardResponse cached, CancellationToken ct)
    {
        var row = await _db.Classes.AsNoTracking()
            .Where(x => x.Id == cached.Class.Id)
            .Select(x => new
            {
                x.Title,
                x.StartsAt,
                x.DurationMinutes,
                x.IsCancelled,
                RoomId = x.Room.Id,
                RoomName = x.Room.Name
            })
            .SingleOrDefaultAsync(ct);

        // решение: поля занятия в версию брони не пишу, сверяю их с базой, иначе карточка врет до конца TTL.
        return row != null
            && row.Title == cached.Class.Title
            && row.StartsAt == cached.Class.StartsAt
            && row.DurationMinutes == cached.Class.DurationMinutes
            && row.IsCancelled == cached.Class.IsCancelled
            && row.RoomId == cached.Class.Room.Id
            && row.RoomName == cached.Class.Room.Name;
    }

    private async Task<long> TakenSeatsAsync(long classId, CancellationToken ct)
    {
        return await _db.Enrollments
            .Where(x => x.ClassId == classId && x.Status == BookingStatus.Active)
            .SumAsync(x => (long?)x.Seats, ct) ?? 0;
    }

    private static BookingResult? RejectUnbookableClass(Class scheduled, DateTime now)
    {
        if (scheduled.IsCancelled)
        {
            return Conflict("Class cancelled", BookingReason.ClassCancelled);
        }

        if (now >= ClockWork.AsUtc(scheduled.StartsAt))
        {
            return Conflict("Class started", BookingReason.ClassStarted);
        }

        return null;
    }

    private static BookingResult? RejectMutableEnrollment(Enrollment enrollment, DateTime now)
    {
        if (enrollment.Status == BookingStatus.Cancelled)
        {
            return Conflict("Enrollment cancelled", BookingReason.EnrollmentCancelled);
        }

        if (enrollment.Class.IsCancelled)
        {
            return Conflict("Class cancelled", BookingReason.ClassCancelled);
        }

        if (now >= ClockWork.AsUtc(enrollment.Class.StartsAt))
        {
            return Conflict("Class started", BookingReason.ClassStarted);
        }

        return null;
    }

    private static BookingResult? RejectPassSpend(Pass pass, int seats, DateTime now)
    {
        if (DateOnly.FromDateTime(now) > pass.ValidUntil)
        {
            return Conflict("Pass expired", BookingReason.PassExpired);
        }

        if (pass.RemainingVisits < seats)
        {
            return Conflict("Pass exhausted", BookingReason.PassExhausted);
        }

        return null;
    }

    private static EnrollmentCardResponse MapCard(Enrollment enrollment, Pass pass)
    {
        return new EnrollmentCardResponse
        {
            Id = enrollment.Id,
            Status = enrollment.Status,
            Seats = enrollment.Seats,
            CreatedAt = ClockWork.AsUtc(enrollment.CreatedAt),
            Class = new ClassResponse
            {
                Id = enrollment.Class.Id,
                Title = enrollment.Class.Title,
                StartsAt = ClockWork.AsUtc(enrollment.Class.StartsAt),
                DurationMinutes = enrollment.Class.DurationMinutes,
                IsCancelled = enrollment.Class.IsCancelled,
                Room = new RoomResponse
                {
                    Id = enrollment.Class.Room.Id,
                    Name = enrollment.Class.Room.Name
                }
            },
            Pass = new PassResponse
            {
                RemainingVisits = pass.RemainingVisits,
                ValidUntil = pass.ValidUntil
            }
        };
    }

    private static CancelEnrollmentResponse CancelBody(Enrollment enrollment)
    {
        return new CancelEnrollmentResponse
        {
            Id = enrollment.Id,
            Status = BookingStatus.Cancelled,
            RefundedVisits = enrollment.RefundedVisits
        };
    }

    private void AddIdempotency(
        long userId,
        string operation,
        string key,
        string requestHash,
        int statusCode,
        string json,
        DateTime now)
    {
        // решение: ключ отдельно у пользователя и у операции.
        _db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            UserId = userId,
            Operation = operation,
            KeyHash = Hashes.Sha256(key),
            Key = key,
            RequestHash = requestHash,
            StatusCode = statusCode,
            ResponseBody = json,
            CreatedAt = now
        });
    }

    private async Task<IdempotencyRecord?> FindIdempotencyAsync(
        long userId,
        string operation,
        string key,
        CancellationToken ct)
    {
        var hash = Hashes.Sha256(key);
        return await _db.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Operation == operation && x.KeyHash == hash, ct);
    }

    private async Task<BookingResult?> StoredAnswerAsync(
        long userId,
        string operation,
        string key,
        string requestHash,
        CancellationToken ct)
    {
        var existing = await FindIdempotencyAsync(userId, operation, key, ct);
        return existing == null ? null : Replay(existing, key, requestHash);
    }

    private static void AddVisits(Pass pass, int delta)
    {
        pass.RemainingVisits = checked(pass.RemainingVisits + delta);
    }

    private static BookingResult Replay(IdempotencyRecord record, string key, string requestHash)
    {
        if (!string.Equals(record.Key, key, StringComparison.Ordinal)
            || !string.Equals(record.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return Conflict("Idempotency key reused", BookingReason.IdempotencyMismatch);
        }

        return BookingResult.Stored(record.StatusCode, record.ResponseBody);
    }

    private static string RequestHash(long classId, int seats)
    {
        return Hashes.Sha256($"{classId}:{seats}");
    }

    private async Task BumpCardsAsync(IEnumerable<long> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToArray();

        if (ids.Length == 0)
        {
            return;
        }

        // решение: версию карточки пишу в ту же транзакцию, что и бронь, иначе сбой Redis оставит старый остаток.
        await _db.Users
            .Where(x => ids.Contains(x.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CardVersion, x => x.CardVersion + 1), ct);
    }

    private static bool IsConstraint(DbUpdateException exception, string name)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is PostgresException postgres
                && postgres.SqlState == PostgresErrorCodes.UniqueViolation
                && postgres.ConstraintName == name)
            {
                return true;
            }
        }

        return false;
    }

    private static BookingResult NotFound()
    {
        return BookingResult.Fail(StatusCodes.Status404NotFound, "Not found");
    }

    private static BookingResult Conflict(string title, string reason)
    {
        return BookingResult.Fail(StatusCodes.Status409Conflict, title, reason);
    }
}
