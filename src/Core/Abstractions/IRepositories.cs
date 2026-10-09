using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;

namespace FaceAttendance.Core.Abstractions;

public interface IPersonRepository
{
    Task<Person?> GetByIdAsync(int id, bool includeTemplates = false, CancellationToken ct = default);

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
    Task<IReadOnlyList<FaceTemplate>> GetActiveForPersonAsync(int personId, CancellationToken ct = default);

    Task<FaceTemplate?> GetActiveForPoseAsync(int personId, FacePose pose, CancellationToken ct = default);

    Task<IReadOnlyList<FaceTemplate>> GetAllForPersonAsync(int personId, CancellationToken ct = default);

    Task AddAsync(FaceTemplate template, CancellationToken ct = default);

    void Remove(FaceTemplate template);

    Task<IReadOnlyList<FaceCandidate>> GetCandidatesAsync(
        string tenantId,
        string providerId,
        int? excludePersonId = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<FaceCandidate>> GetCandidatesForPersonAsync(
        int personId,
        string providerId,
        CancellationToken ct = default);

    Task<int> GetMaxVersionAsync(int personId, FacePose pose, CancellationToken ct = default);

    Task<bool> ImageHashExistsForPersonAsync(int personId, string sha256, FacePose excludePose, CancellationToken ct = default);
}

public interface IAttendanceRepository
{
    Task AddAsync(AttendanceEvent attendanceEvent, CancellationToken ct = default);

    Task<AttendanceEvent?> GetLastAcceptedAsync(int personId, CancellationToken ct = default);

    /// <summary>
    /// Most recent accepted event for a person since <paramref name="notBefore"/>. A null
    /// <paramref name="direction"/> matches any direction, which is what the double-tap guard
    /// needs: the second tap has not been assigned a direction yet.
    /// </summary>
    Task<AttendanceEvent?> GetLastAcceptedInWindowAsync(
        int personId,
        AttendanceDirection? direction,
        DateTimeOffset notBefore,
        CancellationToken ct = default);

    Task<(IReadOnlyList<AttendanceEvent> Items, int Total)> QueryAsync(
        string tenantId,
        int? personId,
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
