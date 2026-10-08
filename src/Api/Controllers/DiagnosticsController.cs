using FaceAttendance.Api.Contracts;
using FaceAttendance.Api.Infrastructure;
using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Faces;
using FaceAttendance.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FaceAttendance.Api.Controllers;

/// <summary>
/// Introspection for operators: which engine is live, what the thresholds are, and what the
/// detector makes of a given frame. Invaluable when someone reports that recognition is wrong.
/// </summary>
[ApiController]
[Route("api/v1/diagnostics")]
[Authorize(Policy = Policies.Admin)]
[Produces("application/json")]
public sealed class DiagnosticsController(
    IFaceRecognitionProvider provider,
    FaceCaptureValidator validator,
    IOptions<FaceRecognitionOptions> options) : ControllerBase
{
    private readonly FaceRecognitionOptions _options = options.Value;

    /// <summary>
    /// The active engine and the numbers it decides by. Worth logging at deploy time: a changed
    /// ProviderId means every existing enrolment needs re-capturing.
    /// </summary>
    [HttpGet("provider")]
    [ProducesResponseType<ProviderInfoResponse>(StatusCodes.Status200OK)]
    public ActionResult<ProviderInfoResponse> GetProvider() => new ProviderInfoResponse(
        provider.ProviderId,
        provider.TemplateKind,
        provider.CompareTemplates(new byte[4], new byte[4]) is not null,
        _options.RequiredPoses,
        _options.VerificationThreshold,
        _options.IdentificationThreshold,
        _options.IdentificationMargin,
        _options.DuplicateEnrollmentThreshold,
        _options.MinQualityScore,
        _options.MinDetectionScore,
        _options.MinImageDimension,
        _options.MinFaceDimension,
        _options.EnforcePoseCheck,
        _options.StoreProbeImages);

    /// <summary>
    /// Run detection and quality scoring on a frame without storing anything. Use it to check
    /// camera placement and lighting before rolling a kiosk out.
    /// </summary>
    [HttpPost("analyze")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    [ProducesResponseType<AnalysisResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AnalysisResponse>> Analyze(IFormFile image, CancellationToken ct)
    {
        FaceImage faceImage = await image.ToFaceImageAsync(_options, ct);

        // The same upload gate the enrolment and attendance paths use. Without it this endpoint
        // would hand a text file to the provider and report a cheerful "face detected".
        validator.ValidateUpload(faceImage);

        FaceCaptureAnalysis analysis = await provider.AnalyzeAsync(faceImage, ct);

        return AnalysisResponse.From(analysis)!;
    }
}
