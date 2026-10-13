using System.Data;
using DataAccessLayer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace WebApi.Bookings;

public static class DbLocks
{
    public static async Task<bool> TryLockClassAsync(ClubDbContext db, long classId, CancellationToken ct)
    {
        var value = await ScalarAsync(db, "SELECT id FROM classes WHERE id = @id FOR UPDATE", ct, ("id", classId));
        return value != null && value != DBNull.Value;
    }

    public static Task LockPassAsync(ClubDbContext db, long userId, CancellationToken ct)
    {
        return ScalarAsync(db, "SELECT id FROM passes WHERE user_id = @id FOR UPDATE", ct, ("id", userId));
    }

    private static async Task<object?> ScalarAsync(
        ClubDbContext db,
        string sql,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        var connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return await command.ExecuteScalarAsync(ct);
    }
}
