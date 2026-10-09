using System.Diagnostics;
using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Common;
using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FaceAttendance.Core.Services;

/// <summary>
/// Verification side: match a live frame against the registered poses and record the outcome.
/// </summary>
/// <remarks>
/// Two modes, and the choice matters more than any threshold:
/// <list type="bullet">
///   <item><b>1:1 verification</b> - the device sends who they claim to be (badge scan, PIN,
///   dropdown) and the face only has to confirm it. Accuracy is roughly independent of
///   headcount.</item>
///   <item><b>1:N identification</b> - the face alone has to pick one person out of everybody.
///   Convenient, but the false-accept rate grows with the size of the population, so the
///   threshold has to rise with headcount and a near-tie is treated as inconclusive.</item>
/// </list>
/// </remarks>
public sealed class AttendanceService(
    IPersonRepository people,
    IFaceTemplateRepository templates,
    IAttendanceRepository attendance,
    IFaceImageStore imageStore,
    IFaceRecognitionProvider provider,
    FaceCaptureValidator validator,
    IUnitOfWork unitOfWork,
    IClock clock,
    IOptions<FaceRecognitionOptions> options,
    ILogger<AttendanceService> logger)
{
    private readonly FaceRecognitionOptions _options = options.Value;

    public async Task<MarkAttendanceResult> MarkAsync(
        MarkAttendanceCommand command,
        bool includeDiagnostics = false,
        CancellationToken ct = default)
    {
        long startedAt = Stopwatch.GetTimestamp();
        DateTimeOffset now = clock.UtcNow;

        validator.ValidateUpload(command.Image);

        Person? claimed = await ResolveClaimedPersonAsync(command, ct);

        // Candidate set: one person for a 1:1 check, the whole tenant for a 1:N search.
        bool oneToOne = claimed is not null;
        double threshold = oneToOne ? _options.VerificationThreshold : _options.IdentificationThreshold;

        if (oneToOne && !claimed!.CanMarkAttendance)
        {
            return await RecordAsync(
                command, now, startedAt, claimed,
                claimed.EnrollmentStatus == EnrollmentStatus.Enrolled
                    ? AttendanceDecision.Rejected
                    : AttendanceDecision.NotEnrolled,
                score: 0, threshold, matchedPose: null, matchedTemplateId: null,
                failureReason: $"Person is {claimed.Status} / {claimed.EnrollmentStatus}.",
                topScores: [], ct: ct);
        }

        IReadOnlyList<FaceCandidate> candidates = oneToOne
            ? await templates.GetCandidatesForPersonAsync(claimed!.Id, provider.ProviderId, ct)
            : await templates.GetCandidatesAsync(command.TenantId, provider.ProviderId, null, ct);

        if (candidates.Count == 0)
        {
            return await RecordAsync(
                command, now, startedAt, claimed, AttendanceDecision.NotEnrolled,
                score: 0, threshold, null, null,
                oneToOne
                    ? "No usable templates for this person under the active provider."
                    : "No enrolled templates in this tenant under the active provider.",
                [], ct);
        }

        FaceMatchResult match;
        try
        {
            match = await provider.MatchAsync(command.Image, candidates, threshold, _options.TopN, ct);
        }
        catch (FaceProcessingException ex) when (ex.Error is FaceError.NoFaceDetected)
        {
            return await RecordAsync(
                command, now, startedAt, claimed, AttendanceDecision.NoFaceDetected,
                0, threshold, null, null, ex.Message, [], ct);
        }
        catch (FaceProcessingException ex) when (ex.Error is FaceError.LowQuality or FaceError.MultipleFacesDetected)
        {
            return await RecordAsync(
                command, now, startedAt, claimed, AttendanceDecision.LowQuality,
                0, threshold, null, null, ex.Message, [], ct);
        }
        catch (FaceProcessingException ex)
        {
            logger.LogError(ex, "Provider failed during attendance match");
            return await RecordAsync(
                command, now, startedAt, claimed, AttendanceDecision.ProviderError,
                0, threshold, null, null, ex.Message, [], ct);
        }

        if (!match.IsMatch)
        {
            return await RecordAsync(
                command, now, startedAt, claimed, AttendanceDecision.Rejected,
                match.Score, threshold, null, null,
                $"Best score {match.Score:F3} did not reach the {threshold:F3} threshold.",
                match.TopScores, ct, includeDiagnostics);
        }

        // A 1:N win that barely beats the runner-up is not a decision, it is a coin toss.
        if (!oneToOne && match.Margin is { } margin && margin < _options.IdentificationMargin)
        {
            return await RecordAsync(
                command, now, startedAt, claimed, AttendanceDecision.Rejected,
                match.Score, threshold, match.Pose, match.TemplateId,
                $"Top two candidates were only {margin:F3} apart; below the required " +
                $"{_options.IdentificationMargin:F3} margin. Ask the user to identify themselves first.",
                match.TopScores, ct, includeDiagnostics);
        }

        Person matched = claimed ?? await people.GetByIdAsync(match.PersonId!.Value, false, ct)
            ?? throw DomainException.NotFound("person_not_found", "The matched person no longer exists.");

        // Collapse accidental double taps BEFORE deciding a direction. Checking afterwards
        // would compare against the inferred direction, which is by definition the opposite of
        // the punch just made - so a second tap two seconds later would sail through as an Out
        // and silently end the person shift.
        if (_options.DuplicatePunchWindowMinutes > 0)
        {
            DateTimeOffset notBefore = now.AddMinutes(-_options.DuplicatePunchWindowMinutes);
            AttendanceEvent? recent = await attendance.GetLastAcceptedInWindowAsync(
                matched.Id, direction: null, notBefore, ct);

            if (recent is not null)
            {
                return await RecordAsync(
                    command, now, startedAt, matched, AttendanceDecision.Duplicate,
                    match.Score, threshold, match.Pose, match.TemplateId,
                    $"Already marked {recent.Direction} at {recent.CreatedAtUtc:O}, inside the " +
                    $"{_options.DuplicatePunchWindowMinutes}-minute window.",
                    match.TopScores, ct, includeDiagnostics, recent.Direction);
            }
        }

        AttendanceDirection direction = await ResolveDirectionAsync(command, matched, ct);

        return await RecordAsync(
            command, now, startedAt, matched, AttendanceDecision.Accepted,
            match.Score, threshold, match.Pose, match.TemplateId, null,
            match.TopScores, ct, includeDiagnostics, direction);
    }

    /// <summary>
    /// Score a frame without recording anything. Useful for a "is the camera seeing me?"
    /// indicator on the capture screen, and for tuning thresholds against real traffic.
    /// </summary>
    public async Task<FaceMatchResult> PreviewMatchAsync(
        string tenantId,
        int? personId,
        FaceImage image,
        CancellationToken ct = default)
    {
        validator.ValidateUpload(image);

        bool oneToOne = personId.HasValue;
        double threshold = oneToOne ? _options.VerificationThreshold : _options.IdentificationThreshold;

        IReadOnlyList<FaceCandidate> candidates = oneToOne
            ? await templates.GetCandidatesForPersonAsync(personId!.Value, provider.ProviderId, ct)
            : await templates.GetCandidatesAsync(tenantId, provider.ProviderId, null, ct);

        return candidates.Count == 0
            ? FaceMatchResult.NoMatch(threshold)
            : await provider.MatchAsync(image, candidates, threshold, _options.TopN, ct);
    }

    private async Task<Person?> ResolveClaimedPersonAsync(MarkAttendanceCommand command, CancellationToken ct)
    {
        if (command.PersonId is { } id)
        {
            Person person = await people.GetByIdAsync(id, false, ct)
                ?? throw DomainException.NotFound("person_not_found", $"Person {id} was not found.");

            if (!string.Equals(person.TenantId, command.TenantId, StringComparison.Ordinal))
            {
                throw DomainException.NotFound("person_not_found", $"Person {id} was not found.");
            }

            return person;
        }

        if (!string.IsNullOrWhiteSpace(command.ExternalId))
        {
            return await people.GetByExternalIdAsync(command.TenantId, command.ExternalId.Trim(), false, ct)
                ?? throw DomainException.NotFound(
                    "person_not_found",
                    $"No person with ExternalId {command.ExternalId} in this tenant.");
        }

        return null;
    }

    /// <summary>
    /// Decide In vs Out. Trusting the client is fine for a kiosk you control, but inferring from
    /// the previous event means a mis-tapped button cannot corrupt the day.
    /// </summary>
    private async Task<AttendanceDirection> ResolveDirectionAsync(
        MarkAttendanceCommand command,
        Person person,
        CancellationToken ct)
    {
        if (!_options.InferDirection && command.Direction is { } explicitDirection)
        {
            return explicitDirection;
        }

        AttendanceEvent? last = await attendance.GetLastAcceptedAsync(person.Id, ct);

        if (last is null)
        {
            return command.Direction ?? AttendanceDirection.In;
        }

        // A new day always starts with an In, even if yesterday ended without an Out.
        bool sameDay = last.CreatedAtUtc.UtcDateTime.Date == clock.UtcNow.UtcDateTime.Date;
        if (!sameDay)
        {
            return AttendanceDirection.In;
        }

        return last.Direction == AttendanceDirection.In
            ? AttendanceDirection.Out
            : AttendanceDirection.In;
    }

    private async Task<MarkAttendanceResult> RecordAsync(
        MarkAttendanceCommand command,
        DateTimeOffset now,
        long startedAt,
        Person? person,
        AttendanceDecision decision,
        double score,
        double threshold,
        FacePose? matchedPose,
        int? matchedTemplateId,
        string? failureReason,
        IReadOnlyList<FaceScore> topScores,
        CancellationToken ct,
        bool includeDiagnostics = false,
        AttendanceDirection? direction = null)
    {
        AttendanceDirection resolvedDirection = direction ?? command.Direction ?? AttendanceDirection.In;

        string? probeKey = null;
        if (_options.StoreProbeImages)
        {
            // Written before the row is inserted, so the row's number does not exist yet. A random
            // token names the file instead; the row records the path, which is what links them.
            probeKey = imageStore.BuildProbeKey(command.TenantId, now, Guid.NewGuid(), command.Image.ContentType);
            try
            {
                await imageStore.SaveAsync(probeKey, command.Image.Content, command.Image.ContentType, ct);
            }
            catch (Exception ex)
            {
                // Losing the audit frame must not lose the punch.
                logger.LogError(ex, "Could not store probe image {Key}", probeKey);
                probeKey = null;
            }
        }

        int elapsedMs = (int)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        AttendanceEvent record = new()
        {
            TenantId = command.TenantId,
            PersonId = person?.Id,
            Direction = resolvedDirection,
            Decision = decision,
            MatchScore = score,
            Threshold = threshold,
            MatchedPose = matchedPose,
            MatchedTemplateId = matchedTemplateId,
            ProviderId = provider.ProviderId,
            ProbeImageObjectKey = probeKey,
            FailureReason = failureReason,
            DeviceId = command.DeviceId,
            Latitude = command.Latitude,
            Longitude = command.Longitude,
            CapturedAtUtc = command.CapturedAtUtc,
            CreatedAtUtc = now,
            ProcessingMs = elapsedMs
        };

        await attendance.AddAsync(record, ct);
        await unitOfWork.SaveChangesAsync(ct);

        if (decision == AttendanceDecision.Accepted)
        {
            logger.LogInformation(
                "Attendance {Direction} accepted for {PersonId} at {Score:F3} in {Ms}ms",
                resolvedDirection, person?.Id, score, elapsedMs);
        }
        else
        {
            logger.LogInformation(
                "Attendance {Decision} for {PersonId}: {Reason} (score {Score:F3}, {Ms}ms)",
                decision, person?.Id, failureReason, score, elapsedMs);
        }

        return new MarkAttendanceResult
        {
            Success = decision == AttendanceDecision.Accepted,
            Decision = decision,
            EventId = record.Id,
            PersonId = person?.Id,
            ExternalId = person?.ExternalId,
            FullName = person?.FullName,
            Direction = decision == AttendanceDecision.Accepted ? resolvedDirection : null,
            Score = score,
            Threshold = threshold,
            MatchedPose = matchedPose,
            RecordedAtUtc = now,
            Message = failureReason,
            ProcessingMs = elapsedMs,
            TopScores = includeDiagnostics ? topScores : []
        };
    }
}
