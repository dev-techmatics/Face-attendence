namespace FaceAttendance.Core.Faces;

public enum FaceError
{
    NoFaceDetected,
    MultipleFacesDetected,
    LowQuality,
    ImageTooSmall,
    ImageTooLarge,
    UnsupportedFormat,
    CorruptImage,
    PoseMismatch,
    DuplicateFrame,
    DuplicateIdentity,
    InconsistentEnrollment,
    ProviderNotConfigured,
    ProviderFailure
}

/// <summary>
/// Raised for every expected, user-correctable failure in the face pipeline. The API maps
/// <see cref="Error"/> onto an HTTP status and a stable machine-readable code so the capture
/// screen can react without string matching.
/// </summary>
public sealed class FaceProcessingException : Exception
{
    public FaceProcessingException(FaceError error, string message, Exception? inner = null)
        : base(message, inner)
    {
        Error = error;
    }

    public FaceError Error { get; }

    public IReadOnlyList<string> Hints { get; init; } = [];
}
