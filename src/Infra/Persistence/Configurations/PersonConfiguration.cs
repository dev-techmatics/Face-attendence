using FaceAttendance.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FaceAttendance.Infrastructure.Persistence.Configurations;

public sealed class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> builder)
    {
        builder.ToTable("People");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.TenantId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(p => p.ExternalId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(p => p.FullName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(p => p.Email).HasMaxLength(256);
        builder.Property(p => p.Department).HasMaxLength(128);

        builder.Property(p => p.Status).HasConversion<int>();
        builder.Property(p => p.EnrollmentStatus).HasConversion<int>();

        builder.Property(p => p.RowVersion).IsRowVersion();

        // The business key the capture screens send. Unique per tenant so a retried
        // registration is a conflict rather than a second identity.
        builder.HasIndex(p => new { p.TenantId, p.ExternalId })
            .IsUnique()
            .HasDatabaseName("UX_People_Tenant_ExternalId");

        // Covers the 1:N candidate fetch, which filters on exactly these columns.
        builder.HasIndex(p => new { p.TenantId, p.EnrollmentStatus, p.Status })
            .HasDatabaseName("IX_People_Tenant_Enrollment_Status");

        builder.HasMany(p => p.FaceTemplates)
            .WithOne(t => t.Person!)
            .HasForeignKey(t => t.PersonId)
            .OnDelete(DeleteBehavior.Cascade);

        // Attendance history outlives the person record on purpose: deleting an employee must
        // not silently rewrite last quarter of attendance.
        builder.HasMany(p => p.AttendanceEvents)
            .WithOne(e => e.Person!)
            .HasForeignKey(e => e.PersonId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
