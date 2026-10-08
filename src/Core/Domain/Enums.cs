namespace FaceAttendance.Core.Domain;

/// <summary>
/// The three capture angles collected during registration. The numeric values are
/// persisted, so never renumber them once data exists.
/// </summary>
public enum FacePose
{
    Front = 1,
    Left = 2,
    Right = 3
}

public enum PersonStatus
{
    Active = 1,
    Suspended = 2,
    Archived = 3
}

public enum EnrollmentStatus
{
    /// <summary>Person record exists, no usable face captured yet.</summary>
    NotEnrolled = 0,

    /// <summary>Some but not all required poses captured.</summary>
    InProgress = 1,

    /// <summary>All required poses captured and cross-validated. Ready for attendance.</summary>
    Enrolled = 2,

    /// <summary>Templates exist but were produced by a provider/model that is no longer active.</summary>
    Stale = 3
}

public enum AttendanceDirection
{
    In = 1,
    Out = 2
}

/// <summary>
/// Outcome of an attendance attempt. Every attempt is persisted, including failures,
/// because the failures are what you need when someone disputes a missing punch.
/// </summary>
public enum AttendanceDecision
{
    Accepted = 1,
    Rejected = 2,
    Duplicate = 3,
    NotEnrolled = 4,
    NoFaceDetected = 5,
    LowQuality = 6,
    SpoofSuspected = 7,
    ProviderError = 8
}
