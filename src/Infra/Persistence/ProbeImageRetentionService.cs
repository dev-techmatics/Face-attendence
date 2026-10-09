using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FaceAttendance.Infrastructure.Persistence;

/// <summary>
/// Deletes attendance probe frames once they age past the configured retention.
/// </summary>
/// <remarks>
/// <para>
/// Probe frames are useful for a few weeks - long enough to settle a disputed punch - and a
/// liability after that. Keeping face images of every employee indefinitely is the kind of
/// detail a data-protection audit asks about, and storage grows by one image per punch per day.
/// </para>
/// <para>
/// Only the image is removed; the attendance row itself is kept, so the register stays complete
/// and <c>ProbeImageObjectKey</c> simply becomes null.
/// </para>
/// </remarks>
public sealed class ProbeImageRetentionService(
    IServiceScopeFactory scopeFactory,
    IOptions<FaceRecognitionOptions> options,
    IClock clock,
    ILogger<ProbeImageRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private const int BatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int retentionDays = options.Value.ProbeImageRetentionDays;

        if (retentionDays <= 0)
        {
            logger.LogInformation("Probe image retention is disabled; frames will be kept indefinitely.");
            return;
        }

        // A short initial delay keeps startup fast and avoids competing with migrations.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        using PeriodicTimer timer = new(Interval);

        do
        {
            try
            {
                await PurgeAsync(retentionDays, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed sweep must not kill the host; the next tick will try again.
                logger.LogError(ex, "Probe image retention sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PurgeAsync(int retentionDays, CancellationToken ct)
    {
        DateTimeOffset cutoff = clock.UtcNow.AddDays(-retentionDays);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IFaceImageStore store = scope.ServiceProvider.GetRequiredService<IFaceImageStore>();

        int deleted = 0;

        // Batched so a long-neglected deployment does not try to delete a year of frames in one
        // transaction.
        while (!ct.IsCancellationRequested)
        {
            var expired = await db.AttendanceEvents
                .Where(e => e.CreatedAtUtc < cutoff && e.ProbeImageObjectKey != null)
                .OrderBy(e => e.CreatedAtUtc)
                .Take(BatchSize)
                .Select(e => new { e.Id, e.ProbeImageObjectKey })
                .ToListAsync(ct);

            if (expired.Count == 0)
            {
                break;
            }

            foreach (var row in expired)
            {
                await store.DeleteAsync(row.ProbeImageObjectKey!, ct);
            }

            // Clear the keys in bulk after the files are gone. Doing it in this order means a
            // crash leaves an orphaned file, not a row pointing at a file that no longer exists.
            List<int> ids = [.. expired.Select(r => r.Id)];

            await db.AttendanceEvents
                .Where(e => ids.Contains(e.Id))
                .ExecuteUpdateAsync(set => set.SetProperty(e => e.ProbeImageObjectKey, _ => null), ct);

            deleted += expired.Count;
        }

        if (deleted > 0)
        {
            logger.LogInformation(
                "Deleted {Count} probe images captured before {Cutoff:yyyy-MM-dd}", deleted, cutoff);
        }
    }
}
