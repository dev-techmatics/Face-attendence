using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;

namespace FaceAttendance.Core.Abstractions;

public interface IPersonRepository
{
    Task<Person?> GetByIdAsync(Guid id, bool includeTemplates = false, CancellationToken ct = default);

    Task<Person?> GetByExternalIdAsync(string tenantId, string externalId, bool includeTemplates = false, CancellationToken ct = default);

    Task<bool> ExternalIdExistsAsync(string tenantId, string externalId, CancellationToken ct = default);

    Task AddAsync(Person person, CancellationToken ct = default);

    void Remove(Person person);

    Task<(IReadOnlyList<Person> Items, int Total)> SearchAsync(
        string tenantId,
        string? query,
        EnrollmentStatus? enrollmentStatus,
        int page,
        int pageSize,
        CancellationToken ct = default);
}

public interface IFaceTemplateRepository
{
    Task<IReadOnlyList<FaceTemplate>> GetActiveForPersonAsync(Guid personId, CancellationToken ct = default);

    Task<FaceTemplate?> GetActiveForPoseAsync(Guid personId, FacePose pose, CancellationToken ct = default);

    Task<IReadOnlyList<FaceTemplate>> GetAllForPersonAsync(Guid personId, CancellationToken ct = default);

    Task AddAsync(FaceTemplate template, CancellationToken ct = default);

    void Remove(FaceTemplate template);

    Task<IReadOnlyList<FaceCandidate>> GetCandidatesAsync(
        string tenantId,
        string providerId,
        Guid? excludePersonId = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<FaceCandidate>> GetCandidatesForPersonAsync(
        Guid personId,
        string providerId,
        CancellationToken ct = default);

    Task<int> GetMaxVersionAsync(Guid personId, FacePose pose, CancellationToken ct = default);

    Task<bool> ImageHashExistsForPersonAsync(Guid personId, string sha256, FacePose excludePose, CancellationToken ct = default);
}

public interface IAttendanceRepository
{
    Task AddAsync(AttendanceEvent attendanceEvent, CancellationToken ct = default);

    Task<AttendanceEvent?> GetLastAcceptedAsync(Guid personId, CancellationToken ct = default);

    /// <summary>
    /// Most recent accepted event for a person since <paramref name="notBefore"/>. A null
    /// <paramref name="direction"/> matches any direction, which is what the double-tap guard
    /// needs: the second tap has not been assigned a direction yet.
    /// </summary>
    Task<AttendanceEvent?> GetLastAcceptedInWindowAsync(
        Guid personId,
        AttendanceDirection? direction,
        DateTimeOffset notBefore,
        CancellationToken ct = default);

    Task<(IReadOnlyList<AttendanceEvent> Items, int Total)> QueryAsync(
        string tenantId,
        Guid? personId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        AttendanceDecision? decision,
        int page,
        int pageSize,
        CancellationToken ct = default);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
