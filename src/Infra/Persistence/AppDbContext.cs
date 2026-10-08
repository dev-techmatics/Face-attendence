using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace FaceAttendance.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IUnitOfWork
{
    public DbSet<Person> People => Set<Person>();

    public DbSet<FaceTemplate> FaceTemplates => Set<FaceTemplate>();

    public DbSet<AttendanceEvent> AttendanceEvents => Set<AttendanceEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // A person must not have two live templates for one pose. The enrollment service already
        // retires the previous row, but a database-level guarantee is what keeps a concurrent
        // double upload from producing two "active" fronts. Filtered-index syntax is dialect
        // specific, so it is only applied where it is known to work.
        if (Database.IsSqlServer())
        {
            modelBuilder.Entity<FaceTemplate>()
                .HasIndex(t => new { t.PersonId, t.Pose })
                .IsUnique()
                .HasFilter("[IsActive] = 1")
                .HasDatabaseName("UX_FaceTemplates_Person_Pose_Active");
        }
    }
}
