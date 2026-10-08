using FaceAttendance.Api.Contracts;
using FaceAttendance.Api.Infrastructure;
using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;
using FaceAttendance.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace FaceAttendance.Api.Controllers;

/// <summary>Verification against the registered poses, and the resulting attendance record.</summary>
[ApiController]
[Route("api/v1/attendance")]
[Produces("application/json")]
public sealed class AttendanceController(
    AttendanceService attendance,
    IAttendanceRepository repository,
    ITenantContext tenant,
    IOptions<FaceRecognitionOptions> options,
    IWebHostEnvironment environment) : ControllerBase
{
    private readonly FaceRecognitionOptions _options = options.Value;

    /// <summary>
    /// Verify a live frame and record the punch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Send <c>personId</c> or <c>externalId</c> whenever the device can obtain it - a badge tap,
    /// a PIN, a name picker. That makes this a 1:1 verification, which is faster and far more
    /// accurate than searching the whole population, because its error rate does not grow with
    /// headcount.
    /// </para>
    /// <para>
    /// Omitting both performs a 1:N identification. Convenient, but the chance of a false accept
    /// rises with the number of enrolled people, so the threshold is stricter and a near-tie
    /// between two candidates is refused rather than guessed.
    /// </para>
    /// <para>
    /// A non-match is still a 200 with <c>success: false</c> and a decision code: the request
    /// was processed correctly, it simply did not match. Reserve 4xx for malformed requests.
    /// </para>
    /// </remarks>
    [HttpPost("mark")]
    [Authorize(Policy = Policies.Device)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    [EnableRateLimiting(RateLimitPolicies.Attendance)]
    [ProducesResponseType<MarkAttendanceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MarkAttendanceResponse>> Mark(
        [FromForm] MarkAttendanceRequest request,
        CancellationToken ct)
    {
        FaceImage image = await request.Image.ToFaceImageAsync(_options, ct);

        MarkAttendanceResult result = await attendance.MarkAsync(
            new MarkAttendanceCommand
            {
                TenantId = tenant.TenantId,
                PersonId = request.PersonId,
                ExternalId = request.ExternalId,
                Image = image,
                Direction = request.Direction,
                DeviceId = request.DeviceId,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                CapturedAtUtc = request.CapturedAtUtc
            },
            // Runner-up scores are useful when tuning but would tell a kiosk operator who else
            // the camera nearly matched, which is not theirs to know.
            includeDiagnostics: environment.IsDevelopment(),
            ct);

        return MarkAttendanceResponse.From(result);
    }

    /// <summary>
    /// Score a frame without recording anything. Use it for a live "ready to punch" indicator,
    /// and for measuring real score distributions before settling on a threshold.
    /// </summary>
    [HttpPost("preview")]
    [Authorize(Policy = Policies.Admin)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    [ProducesResponseType<PreviewMatchResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PreviewMatchResponse>> Preview(
        [FromForm] PreviewMatchRequest request,
        CancellationToken ct)
    {
        FaceImage image = await request.Image.ToFaceImageAsync(_options, ct);

        FaceMatchResult match = await attendance.PreviewMatchAsync(
            tenant.TenantId, request.PersonId, image, ct);

        return new PreviewMatchResponse(
            match.IsMatch,
            match.PersonId,
            match.Pose,
            Math.Round(match.Score, 4),
            Math.Round(match.Threshold, 4),
            match.Margin is { } m ? Math.Round(m, 4) : null,
            [.. match.TopScores.Select(s => new ScoreResponse(s.PersonId, s.Pose, Math.Round(s.Score, 4)))],
            AnalysisResponse.From(match.ProbeAnalysis));
    }

    /// <summary>
    /// The attendance register, including rejected attempts. Those failures are the audit trail
    /// you need when somebody says the machine did not recognise them.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<PagedResponse<AttendanceEventResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<AttendanceEventResponse>>> Query(
        [FromQuery] Guid? personId,
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] AttendanceDecision? decision,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        (IReadOnlyList<AttendanceEvent> items, int total) = await repository.QueryAsync(
            tenant.TenantId, personId, fromUtc, toUtc, decision, page, pageSize, ct);

        return new PagedResponse<AttendanceEventResponse>(
            [.. items.Select(AttendanceEventResponse.From)], page, pageSize, total);
    }
}
