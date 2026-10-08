namespace FaceAttendance.Api.Infrastructure;

public static class Policies
{
    /// <summary>Full access, including enrolling faces and reading stored images.</summary>
    public const string Admin = "Admin";

    /// <summary>
    /// Marking attendance only. Kiosk and mobile keys get this; they are the keys most likely to
    /// be extracted from a device, so they must not be able to enrol a face or read a photo.
    /// </summary>
    public const string Device = "Device";
}

public static class RateLimitPolicies
{
    /// <summary>
    /// Throttles the attendance endpoint. A kiosk submitting frames in a loop is the normal
    /// failure mode, and each call costs an inference.
    /// </summary>
    public const string Attendance = "attendance";
}
