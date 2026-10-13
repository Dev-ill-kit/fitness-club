using Common.Database.Interfaces;

namespace DataAccessLayer.Entities;

public class IdempotencyRecord : IEntity
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public string Operation { get; set; } = null!;

    public string KeyHash { get; set; } = null!;

    public string Key { get; set; } = null!;

    public string RequestHash { get; set; } = null!;

    public int StatusCode { get; set; }

    public string ResponseBody { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}
