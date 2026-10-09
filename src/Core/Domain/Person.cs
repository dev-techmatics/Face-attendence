namespace FaceAttendance.Core.Domain;

/// <summary>
/// A person who can be registered and later recognised. <see cref="ExternalId"/> is the id
/// your front end already knows (employee code, student roll number, ...) and is what the
/// capture screens send for all three pose uploads.
/// </summary>
public class Person
{
    /// <summary>Assigned by SQL Server on insert (IDENTITY, starting at 10001). Never set it in code.</summary>
    public int Id { get; set; }

    /// <summary>Tenant / organisation scope. Keeps one deployment usable by several sites.</summary>
    public string TenantId { get; set; } = "default";

    /// <summary>Stable business identifier, unique per tenant.</summary>
    public string ExternalId { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? Department { get; set; }

    public PersonStatus Status { get; set; } = PersonStatus.Active;

    public EnrollmentStatus EnrollmentStatus { get; set; } = EnrollmentStatus.NotEnrolled;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? EnrolledAtUtc { get; set; }

    /// <summary>Optimistic concurrency guard (rowversion on SQL Server).</summary>
    public byte[]? RowVersion { get; set; }

    public ICollection<FaceTemplate> FaceTemplates { get; set; } = [];

    public ICollection<AttendanceEvent> AttendanceEvents { get; set; } = [];

    public bool CanMarkAttendance =>
        Status == PersonStatus.Active && EnrollmentStatus == EnrollmentStatus.Enrolled;
}
