using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Domain;
using FaceAttendance.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FaceAttendance.Infrastructure.Repositories;

public sealed class PersonRepository(AppDbContext db) : IPersonRepository
{
    public async Task<Person?> GetByIdAsync(Guid id, bool includeTemplates = false, CancellationToken ct = default)
    {
        IQueryable<Person> query = db.People;

        if (includeTemplates)
        {
            query = query.Include(p => p.FaceTemplates.Where(t => t.IsActive));
        }

        return await query.FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<Person?> GetByExternalIdAsync(
        string tenantId,
        string externalId,
        bool includeTemplates = false,
        CancellationToken ct = default)
    {
        IQueryable<Person> query = db.People;

        if (includeTemplates)
        {
            query = query.Include(p => p.FaceTemplates.Where(t => t.IsActive));
        }

        return await query.FirstOrDefaultAsync(
            p => p.TenantId == tenantId && p.ExternalId == externalId, ct);
    }

    public Task<bool> ExternalIdExistsAsync(string tenantId, string externalId, CancellationToken ct = default) =>
        db.People.AnyAsync(p => p.TenantId == tenantId && p.ExternalId == externalId, ct);

    public async Task AddAsync(Person person, CancellationToken ct = default) =>
        await db.People.AddAsync(person, ct);

    public void Remove(Person person) => db.People.Remove(person);

    public async Task<(IReadOnlyList<Person> Items, int Total)> SearchAsync(
        string tenantId,
        string? query,
        EnrollmentStatus? enrollmentStatus,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        IQueryable<Person> q = db.People.Where(p => p.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(query))
        {
            string term = query.Trim();
            q = q.Where(p => p.FullName.Contains(term) || p.ExternalId.Contains(term));
        }

        if (enrollmentStatus is { } status)
        {
            q = q.Where(p => p.EnrollmentStatus == status);
        }

        int total = await q.CountAsync(ct);

        List<Person> items = await q
            .OrderBy(p => p.FullName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync(ct);

        return (items, total);
    }
}
