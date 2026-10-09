using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace FaceAttendance.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IUnitOfWork
{
    /// <summary>First id handed out in every table.</summary>
    public const long IdentitySeed = 10001;

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
            // Short, readable, auto-incrementing ids (10001, 10002, ...) assigned by SQL Server on
            // insert. Starting at five digits keeps them a uniform width for a long time. Expect
            // occasional gaps: a rolled-back registration still consumes a number, and SQL Server
            // may skip ahead after a restart. Never rely on the numbers being consecutive.
            modelBuilder.Entity<Person>().Property(p => p.Id).UseIdentityColumn(IdentitySeed, 1);
            modelBuilder.Entity<FaceTemplate>().Property(t => t.Id).UseIdentityColumn(IdentitySeed, 1);
            modelBuilder.Entity<AttendanceEvent>().Property(e => e.Id).UseIdentityColumn(IdentitySeed, 1);

            modelBuilder.Entity<FaceTemplate>()
                .HasIndex(t => new { t.PersonId, t.Pose })
                .IsUnique()
                .HasFilter("[IsActive] = 1")
                .HasDatabaseName("UX_FaceTemplates_Person_Pose_Active");
        }
    }
}
