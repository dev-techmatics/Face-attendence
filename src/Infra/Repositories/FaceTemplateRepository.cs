using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;
using FaceAttendance.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FaceAttendance.Infrastructure.Repositories;

public sealed class FaceTemplateRepository(AppDbContext db) : IFaceTemplateRepository
{
    public async Task<IReadOnlyList<FaceTemplate>> GetActiveForPersonAsync(
        int personId,
        CancellationToken ct = default) =>
        await db.FaceTemplates
            .Where(t => t.PersonId == personId && t.IsActive)
            .OrderBy(t => t.Pose)
            .ToListAsync(ct);

    public Task<FaceTemplate?> GetActiveForPoseAsync(
        int personId,
        FacePose pose,
        CancellationToken ct = default) =>
        db.FaceTemplates.FirstOrDefaultAsync(
            t => t.PersonId == personId && t.Pose == pose && t.IsActive, ct);

    public async Task<IReadOnlyList<FaceTemplate>> GetAllForPersonAsync(
        int personId,
        CancellationToken ct = default) =>
        await db.FaceTemplates
            .Where(t => t.PersonId == personId)
            .ToListAsync(ct);

    public async Task AddAsync(FaceTemplate template, CancellationToken ct = default) =>
        await db.FaceTemplates.AddAsync(template, ct);

    public void Remove(FaceTemplate template) => db.FaceTemplates.Remove(template);

    /// <summary>
    /// The 1:N candidate set. Projected straight to <see cref="FaceCandidate"/> and read
    /// untracked: the change tracker has no business holding thousands of embeddings.
    /// </summary>
    public async Task<IReadOnlyList<FaceCandidate>> GetCandidatesAsync(
        string tenantId,
        string providerId,
        int? excludePersonId = null,
        CancellationToken ct = default)
    {
        IQueryable<FaceTemplate> query = db.FaceTemplates
            .Where(t => t.IsActive
                        && t.ProviderId == providerId
                        && t.Person!.TenantId == tenantId
                        && t.Person.Status == PersonStatus.Active);

        if (excludePersonId is { } exclude)
        {
            query = query.Where(t => t.PersonId != exclude);
        }

        return await query
            .AsNoTracking()
            .Select(t => new FaceCandidate
            {
                PersonId = t.PersonId,
                TemplateId = t.Id,
                Pose = t.Pose,
                TemplateData = t.TemplateData,
                ImageObjectKey = t.ImageObjectKey
            })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<FaceCandidate>> GetCandidatesForPersonAsync(
        int personId,
        string providerId,
        CancellationToken ct = default) =>
        await db.FaceTemplates
            .Where(t => t.PersonId == personId && t.IsActive && t.ProviderId == providerId)
            .AsNoTracking()
            .Select(t => new FaceCandidate
            {
                PersonId = t.PersonId,
                TemplateId = t.Id,
                Pose = t.Pose,
                TemplateData = t.TemplateData,
                ImageObjectKey = t.ImageObjectKey
            })
            .ToListAsync(ct);

    public async Task<int> GetMaxVersionAsync(
        int personId,
        FacePose pose,
        CancellationToken ct = default)
    {
        // Counts retired rows too, so versions never repeat for a person/pose pair.
        bool any = await db.FaceTemplates.AnyAsync(t => t.PersonId == personId && t.Pose == pose, ct);

        return any
            ? await db.FaceTemplates
                .Where(t => t.PersonId == personId && t.Pose == pose)
                .MaxAsync(t => t.Version, ct)
            : 0;
    }

    public Task<bool> ImageHashExistsForPersonAsync(
        int personId,
        string sha256,
        FacePose excludePose,
        CancellationToken ct = default) =>
        db.FaceTemplates.AnyAsync(
            t => t.PersonId == personId
                 && t.IsActive
                 && t.Pose != excludePose
                 && t.ImageSha256 == sha256,
            ct);
}
