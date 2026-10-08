using FaceAttendance.Core.Common;
using FaceAttendance.Core.Faces;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FaceAttendance.Api.Infrastructure;

/// <summary>
/// Turns the two expected exception families into RFC 9457 problem responses with a stable
/// machine-readable code, so a capture screen can branch on the code instead of parsing prose.
/// </summary>
public sealed class FaceExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<FaceExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>499 is the nginx convention for a client that went away; ASP.NET has no constant.</summary>
    private const int ClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken ct)
    {
        // safeDetail marks messages this codebase wrote on purpose and can show a caller.
        // Anything unrecognised gets a generic message instead: stack traces and connection
        // strings have a habit of ending up in exception text.
        (int status, string code, string title, IReadOnlyList<string> hints, bool safeDetail) = exception switch
        {
            FaceProcessingException face => (
                StatusFor(face.Error),
                face.Error.ToString().ToSnakeCase(),
                "The image could not be used",
                face.Hints,
                true),

            DomainException domain => (
                domain.Kind switch
                {
                    DomainErrorKind.NotFound => StatusCodes.Status404NotFound,
                    DomainErrorKind.Conflict => StatusCodes.Status409Conflict,
                    DomainErrorKind.Forbidden => StatusCodes.Status403Forbidden,
                    _ => StatusCodes.Status400BadRequest
                },
                domain.Code,
                "The request could not be completed",
                (IReadOnlyList<string>)[],
                true),

            BadHttpRequestException => (
                StatusCodes.Status400BadRequest,
                "malformed_request",
                "The request could not be read",
                (IReadOnlyList<string>)[],
                false),

            OperationCanceledException => (
                ClientClosedRequest,
                "request_cancelled",
                "The request was cancelled",
                (IReadOnlyList<string>)[],
                false),

            _ => (
                StatusCodes.Status500InternalServerError,
                "internal_error",
                "Something went wrong",
                (IReadOnlyList<string>)[],
                false)
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
        }
        else
        {
            logger.LogInformation(
                "{Code} on {Method} {Path}: {Message}",
                code, context.Request.Method, context.Request.Path, exception.Message);
        }

        context.Response.StatusCode = status;

        // Curated messages are shown whatever the status code. A 503 saying "detector model not
        // found at <path>" is exactly what the operator needs; replacing it with "an unexpected
        // error occurred" turns a two-minute fix into an afternoon of log archaeology.
        string detail = safeDetail
            ? exception.Message
            : "An unexpected error occurred. The incident has been logged.";

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
                Extensions =
                {
                    ["code"] = code,
                    ["hints"] = hints,
                    ["traceId"] = context.TraceIdentifier
                }
            }
        });
    }

    private static int StatusFor(FaceError error) => error switch
    {
        FaceError.ImageTooLarge => StatusCodes.Status413PayloadTooLarge,
        FaceError.UnsupportedFormat => StatusCodes.Status415UnsupportedMediaType,

        // Conflicts, not bad requests: the upload was fine, it collided with existing state.
        FaceError.DuplicateIdentity or FaceError.DuplicateFrame => StatusCodes.Status409Conflict,

        // The caller cannot fix a missing model file by changing the request.
        FaceError.ProviderNotConfigured => StatusCodes.Status503ServiceUnavailable,
        FaceError.ProviderFailure => StatusCodes.Status502BadGateway,

        _ => StatusCodes.Status422UnprocessableEntity
    };
}

internal static class StringExtensions
{
    /// <summary>NoFaceDetected becomes no_face_detected, so clients get a stable code.</summary>
    public static string ToSnakeCase(this string value)
    {
        System.Text.StringBuilder builder = new(value.Length + 8);

        for (int i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(value[i]));
        }

        return builder.ToString();
    }

}
