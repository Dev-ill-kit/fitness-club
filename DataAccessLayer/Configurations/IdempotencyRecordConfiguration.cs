using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_keys");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.Operation).HasColumnName("operation").IsRequired();
        builder.Property(x => x.KeyHash).HasColumnName("key_hash").IsRequired();
        builder.Property(x => x.Key).HasColumnName("idempotency_key").IsRequired();
        builder.Property(x => x.RequestHash).HasColumnName("request_hash").IsRequired();
        builder.Property(x => x.StatusCode).HasColumnName("status_code");
        builder.Property(x => x.ResponseBody).HasColumnName("response_body").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");

        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId);

        builder
            .HasIndex(x => new { x.UserId, x.Operation, x.KeyHash })
            .IsUnique()
            .HasDatabaseName("ux_idempotency_user_operation_key");
    }
}
