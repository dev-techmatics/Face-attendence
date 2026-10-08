namespace FaceAttendance.Core.Common;

public enum DomainErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Forbidden
}

/// <summary>
/// Application-level failure with an intended HTTP shape. Kept separate from
/// FaceProcessingException so the API can distinguish "your request was wrong" from
/// "the image was not usable".
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(DomainErrorKind kind, string code, string message)
        : base(message)
    {
        Kind = kind;
        Code = code;
    }

    public DomainErrorKind Kind { get; }

    /// <summary>Stable machine-readable code, e.g. <c>person_not_found</c>.</summary>
    public string Code { get; }

    public static DomainException NotFound(string code, string message) =>
        new(DomainErrorKind.NotFound, code, message);

    public static DomainException Conflict(string code, string message) =>
        new(DomainErrorKind.Conflict, code, message);

    public static DomainException Validation(string code, string message) =>
        new(DomainErrorKind.Validation, code, message);

    public static DomainException Forbidden(string code, string message) =>
        new(DomainErrorKind.Forbidden, code, message);
}
