using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Domain;
using FaceAttendance.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FaceAttendance.Infrastructure.Repositories;

public sealed class AttendanceRepository(AppDbContext db) : IAttendanceRepository
{
    public async Task AddAsync(AttendanceEvent attendanceEvent, CancellationToken ct = default) =>
        await db.AttendanceEvents.AddAsync(attendanceEvent, ct);

    public Task<AttendanceEvent?> GetLastAcceptedAsync(Guid personId, CancellationToken ct = default) =>
        db.AttendanceEvents
            .Where(e => e.PersonId == personId && e.Decision == AttendanceDecision.Accepted)
            .OrderByDescending(e => e.CreatedAtUtc)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

    public Task<AttendanceEvent?> GetLastAcceptedInWindowAsync(
        Guid personId,
        AttendanceDirection? direction,
        DateTimeOffset notBefore,
        CancellationToken ct = default) =>
        db.AttendanceEvents
            .Where(e => e.PersonId == personId
                        && e.Decision == AttendanceDecision.Accepted
                        && e.CreatedAtUtc >= notBefore
                        && (direction == null || e.Direction == direction))
            .OrderByDescending(e => e.CreatedAtUtc)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

    public async Task<(IReadOnlyList<AttendanceEvent> Items, int Total)> QueryAsync(
        string tenantId,
        Guid? personId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        AttendanceDecision? decision,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        IQueryable<AttendanceEvent> q = db.AttendanceEvents.Where(e => e.TenantId == tenantId);

        if (personId is { } id)
        {
            q = q.Where(e => e.PersonId == id);
        }

        if (fromUtc is { } from)
        {
            q = q.Where(e => e.CreatedAtUtc >= from);
        }

        if (toUtc is { } to)
        {
            q = q.Where(e => e.CreatedAtUtc < to);
        }

        if (decision is { } d)
        {
            q = q.Where(e => e.Decision == d);
        }

        int total = await q.CountAsync(ct);

        List<AttendanceEvent> items = await q
            .OrderByDescending(e => e.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(e => e.Person)
            .AsNoTracking()
            .ToListAsync(ct);

        return (items, total);
    }
}
