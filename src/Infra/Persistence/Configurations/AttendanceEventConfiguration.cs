using FaceAttendance.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FaceAttendance.Infrastructure.Persistence.Configurations;

public sealed class AttendanceEventConfiguration : IEntityTypeConfiguration<AttendanceEvent>
{
    public void Configure(EntityTypeBuilder<AttendanceEvent> builder)
    {
        builder.ToTable("AttendanceEvents");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Direction).HasConversion<int>();
        builder.Property(e => e.Decision).HasConversion<int>();
        builder.Property(e => e.MatchedPose).HasConversion<int?>();
        builder.Property(e => e.ProviderId).HasMaxLength(128).IsRequired();
        builder.Property(e => e.ProbeImageObjectKey).HasMaxLength(512);
        builder.Property(e => e.FailureReason).HasMaxLength(1024);
        builder.Property(e => e.DeviceId).HasMaxLength(128);

        // The report query: one person, one date range, newest first.
        builder.HasIndex(e => new { e.PersonId, e.CreatedAtUtc })
            .HasDatabaseName("IX_AttendanceEvents_Person_CreatedAt");

        // The tenant-wide daily register.
        builder.HasIndex(e => new { e.TenantId, e.CreatedAtUtc })
            .HasDatabaseName("IX_AttendanceEvents_Tenant_CreatedAt");

        // Lets the retention job find probe frames to delete without a table scan.
        builder.HasIndex(e => e.CreatedAtUtc)
            .HasDatabaseName("IX_AttendanceEvents_CreatedAt");
    }
}
