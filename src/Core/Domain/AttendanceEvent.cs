namespace FaceAttendance.Core.Domain;

/// <summary>
/// Audit record of a single attendance attempt - accepted or not. Written for every call so
/// that disputes ("the machine did not recognise me") can be investigated after the fact.
/// </summary>
public class AttendanceEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string TenantId { get; set; } = "default";

    /// <summary>Null when a 1:N identification found nobody.</summary>
    public Guid? PersonId { get; set; }

    public Person? Person { get; set; }

    public AttendanceDirection Direction { get; set; }

    public AttendanceDecision Decision { get; set; }

    /// <summary>Similarity of the best candidate, in the provider's own scale.</summary>
    public double MatchScore { get; set; }

    /// <summary>Threshold in force at decision time. Stored so later tuning stays explainable.</summary>
    public double Threshold { get; set; }

    public FacePose? MatchedPose { get; set; }

    public Guid? MatchedTemplateId { get; set; }

    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Stored probe frame, when probe retention is enabled.</summary>
    public string? ProbeImageObjectKey { get; set; }

    public string? FailureReason { get; set; }

    public string? DeviceId { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    /// <summary>When the device captured the frame (client clock, untrusted).</summary>
    public DateTimeOffset? CapturedAtUtc { get; set; }

    /// <summary>When the server recorded the event (authoritative).</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    public int ProcessingMs { get; set; }
}
