using System.ComponentModel.DataAnnotations;
using FaceAttendance.Core.Domain;

namespace FaceAttendance.Api.Contracts;

public sealed class CreatePersonRequest
{
    /// <summary>The id your own system already uses: employee code, roll number, badge number.</summary>
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string ExternalId { get; init; } = string.Empty;

    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string FullName { get; init; } = string.Empty;

    [EmailAddress]
    [StringLength(256)]
    public string? Email { get; init; }

    [StringLength(128)]
    public string? Department { get; init; }
}

/// <summary>One pose upload for an existing person.</summary>
public sealed class CaptureFaceRequest
{
    [Required]
    public FacePose Pose { get; init; }

    [Required]
    public IFormFile Image { get; init; } = default!;
}

/// <summary>
/// All three poses in a single request, which is usually what a capture wizard wants: the
/// person is still in front of the camera, and a partial enrolment is never left behind.
/// </summary>
public sealed class CaptureAllPosesRequest
{
    [Required]
    public IFormFile Front { get; init; } = default!;

    [Required]
    public IFormFile Left { get; init; } = default!;

    [Required]
    public IFormFile Right { get; init; } = default!;
}

/// <summary>Create the person and store all three poses in one call.</summary>
public sealed class RegisterPersonRequest
{
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string ExternalId { get; init; } = string.Empty;

    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string FullName { get; init; } = string.Empty;

    [EmailAddress]
    [StringLength(256)]
    public string? Email { get; init; }

    [StringLength(128)]
    public string? Department { get; init; }

    [Required]
    public IFormFile Front { get; init; } = default!;

    [Required]
    public IFormFile Left { get; init; } = default!;

    [Required]
    public IFormFile Right { get; init; } = default!;
}

public sealed class MarkAttendanceRequest
{
    [Required]
    public IFormFile Image { get; init; } = default!;

    /// <summary>
    /// Who the device thinks this is. Supplying either this or <see cref="ExternalId"/> turns
    /// the call into a 1:1 verification, which is both faster and markedly more accurate than
    /// searching the whole population.
    /// </summary>
    public int? PersonId { get; init; }

    public string? ExternalId { get; init; }

    /// <summary>Omit to let the server infer In or Out from the previous event.</summary>
    public AttendanceDirection? Direction { get; init; }

    [StringLength(128)]
    public string? DeviceId { get; init; }

    [Range(-90, 90)]
    public double? Latitude { get; init; }

    [Range(-180, 180)]
    public double? Longitude { get; init; }

    /// <summary>Device capture time. Recorded for reference but never trusted for ordering.</summary>
    public DateTimeOffset? CapturedAtUtc { get; init; }
}

public sealed class PreviewMatchRequest
{
    [Required]
    public IFormFile Image { get; init; } = default!;

    public int? PersonId { get; init; }
}
