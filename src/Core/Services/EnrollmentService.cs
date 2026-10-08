using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Common;
using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FaceAttendance.Core.Services;

/// <summary>
/// Registration side of the system: create a person, then attach one face template per pose,
/// all under the same person id.
/// </summary>
public sealed class EnrollmentService(
    IPersonRepository people,
    IFaceTemplateRepository templates,
    IFaceImageStore imageStore,
    IFaceRecognitionProvider provider,
    FaceCaptureValidator validator,
    IUnitOfWork unitOfWork,
    IClock clock,
    IOptions<FaceRecognitionOptions> options,
    ILogger<EnrollmentService> logger)
{
    private readonly FaceRecognitionOptions _options = options.Value;

    public async Task<Person> CreatePersonAsync(CreatePersonCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.ExternalId))
        {
            throw DomainException.Validation("external_id_required", "ExternalId is required.");
        }

        if (string.IsNullOrWhiteSpace(command.FullName))
        {
            throw DomainException.Validation("full_name_required", "FullName is required.");
        }

        string externalId = command.ExternalId.Trim();

        if (await people.ExternalIdExistsAsync(command.TenantId, externalId, ct))
        {
            throw DomainException.Conflict(
                "external_id_exists",
                $"A person with ExternalId {externalId} already exists in this tenant.");
        }

        DateTimeOffset now = clock.UtcNow;
        Person person = new()
        {
            TenantId = command.TenantId,
            ExternalId = externalId,
            FullName = command.FullName.Trim(),
            Email = command.Email?.Trim(),
            Department = command.Department?.Trim(),
            Status = PersonStatus.Active,
            EnrollmentStatus = EnrollmentStatus.NotEnrolled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await people.AddAsync(person, ct);
        await unitOfWork.SaveChangesAsync(ct);

        logger.LogInformation(
            "Created person {PersonId} ({ExternalId}) in tenant {TenantId}",
            person.Id, person.ExternalId, person.TenantId);

        return person;
    }

    /// <summary>
    /// Store one pose for a person. Calling it again for the same pose supersedes the previous
    /// capture rather than duplicating it, so a retry from the capture screen is always safe.
    /// </summary>
    public async Task<CaptureFaceResult> CaptureFaceAsync(CaptureFaceCommand command, CancellationToken ct = default)
    {
        Person person = await LoadPersonAsync(command.TenantId, command.PersonId, ct);

        if (person.Status != PersonStatus.Active)
        {
            throw DomainException.Conflict(
                "person_not_active",
                $"Person {person.Id} is {person.Status} and cannot be enrolled.");
        }

        validator.ValidateUpload(command.Image);

        // The same frame sent for Front and then Left would otherwise produce an enrolment that
        // looks complete while carrying no angular variety at all.
        if (_options.RejectDuplicateFrames &&
            await templates.ImageHashExistsForPersonAsync(person.Id, command.Image.Sha256, command.Pose, ct))
        {
            throw new FaceProcessingException(
                FaceError.DuplicateFrame,
                "This exact image is already stored for a different pose of this person.")
            {
                Hints = ["Capture a fresh frame for each of the three angles."]
            };
        }

        FaceTemplateMaterial material = await provider.CreateTemplateAsync(command.Image, command.Pose, ct);

        if (material.Analysis is { } analysis)
        {
            validator.ValidateAnalysis(analysis);
            validator.ValidatePose(analysis, command.Pose);
        }

        await GuardAgainstDuplicateIdentityAsync(person, material, ct);

        int version = await templates.GetMaxVersionAsync(person.Id, command.Pose, ct) + 1;

        string objectKey = imageStore.BuildEnrollmentKey(
            person.TenantId, person.Id, command.Pose.ToString(), version, command.Image.ContentType);

        await imageStore.SaveAsync(objectKey, command.Image.Content, command.Image.ContentType, ct);

        FaceTemplate? previous = await templates.GetActiveForPoseAsync(person.Id, command.Pose, ct);
        if (previous is not null)
        {
            previous.IsActive = false;
        }

        FaceTemplate template = new()
        {
            PersonId = person.Id,
            Pose = command.Pose,
            ProviderId = material.ProviderId,
            Dimension = material.Dimension,
            TemplateData = material.Data,
            QualityScore = material.QualityScore,
            DetectionScore = material.DetectionScore,
            ImageObjectKey = objectKey,
            ImageContentType = command.Image.ContentType,
            ImageSizeBytes = command.Image.SizeBytes,
            ImageSha256 = command.Image.Sha256,
            ImageWidth = material.Analysis?.ImageWidth ?? 0,
            ImageHeight = material.Analysis?.ImageHeight ?? 0,
            IsActive = true,
            Version = version,
            CreatedAtUtc = clock.UtcNow
        };

        await templates.AddAsync(template, ct);
        person.UpdatedAtUtc = clock.UtcNow;
        await unitOfWork.SaveChangesAsync(ct);

        // Re-read so the status decision sees exactly what the database now holds.
        IReadOnlyList<FaceTemplate> active = await templates.GetActiveForPersonAsync(person.Id, ct);
        EnrollmentEvaluation evaluation = Evaluate(active);

        bool completed = evaluation.Status == EnrollmentStatus.Enrolled &&
                         person.EnrollmentStatus != EnrollmentStatus.Enrolled;

        person.EnrollmentStatus = evaluation.Status;
        person.EnrolledAtUtc = evaluation.Status == EnrollmentStatus.Enrolled
            ? person.EnrolledAtUtc ?? clock.UtcNow
            : null;
        person.UpdatedAtUtc = clock.UtcNow;
        await unitOfWork.SaveChangesAsync(ct);

        logger.LogInformation(
            "Captured {Pose} v{Version} for person {PersonId}; enrollment is now {Status}",
            command.Pose, version, person.Id, evaluation.Status);

        return new CaptureFaceResult
        {
            PersonId = person.Id,
            Pose = command.Pose,
            TemplateId = template.Id,
            Version = version,
            ImageObjectKey = objectKey,
            Analysis = material.Analysis,
            EnrollmentStatus = evaluation.Status,
            CapturedPoses = [.. active.Select(t => t.Pose).Distinct().OrderBy(p => p)],
            MissingPoses = evaluation.MissingPoses,
            EnrollmentCompleted = completed,
            ReplacedPrevious = previous is not null,
            Warnings = [.. material.Analysis?.Warnings ?? [], .. evaluation.Warnings]
        };
    }

    /// <summary>
    /// Store several poses in one request. Each is validated independently; the first failure
    /// stops the batch, leaving the poses already accepted in place.
    /// </summary>
    public async Task<IReadOnlyList<CaptureFaceResult>> CaptureManyAsync(
        string tenantId,
        Guid personId,
        IReadOnlyList<(FacePose Pose, FaceImage Image)> captures,
        CancellationToken ct = default)
    {
        if (captures.Count == 0)
        {
            throw DomainException.Validation("no_images", "At least one image is required.");
        }

        FacePose[] repeated =
        [
            .. captures.GroupBy(c => c.Pose).Where(g => g.Count() > 1).Select(g => g.Key)
        ];

        if (repeated.Length > 0)
        {
            throw DomainException.Validation(
                "duplicate_pose",
                $"Each pose may appear once per request; repeated: {string.Join(", ", repeated)}.");
        }

        List<CaptureFaceResult> results = new(captures.Count);

        // Front first: it is the pose the duplicate-identity check is most reliable on, so
        // someone already registered under another id is caught before profiles are stored.
        foreach ((FacePose pose, FaceImage image) in captures.OrderBy(c => c.Pose == FacePose.Front ? 0 : 1))
        {
            results.Add(await CaptureFaceAsync(new CaptureFaceCommand(tenantId, personId, pose, image), ct));
        }

        return results;
    }

    public async Task<EnrollmentSummary> GetEnrollmentAsync(
        string tenantId,
        Guid personId,
        CancellationToken ct = default)
    {
        Person person = await LoadPersonAsync(tenantId, personId, ct);
        IReadOnlyList<FaceTemplate> active = await templates.GetActiveForPersonAsync(personId, ct);
        EnrollmentEvaluation evaluation = Evaluate(active);

        List<PoseSimilarity> similarities = [];
        for (int i = 0; i < active.Count; i++)
        {
            for (int j = i + 1; j < active.Count; j++)
            {
                if (SafeCompare(active[i], active[j]) is { } score)
                {
                    similarities.Add(new PoseSimilarity(active[i].Pose, active[j].Pose, score));
                }
            }
        }

        return new EnrollmentSummary
        {
            PersonId = person.Id,
            ExternalId = person.ExternalId,
            FullName = person.FullName,
            Status = person.Status,
            EnrollmentStatus = person.EnrollmentStatus,
            EnrolledAtUtc = person.EnrolledAtUtc,
            Poses =
            [
                .. active.OrderBy(t => t.Pose).Select(t => new CapturedPoseInfo(
                    t.Pose, t.Id, t.Version, t.QualityScore, t.DetectionScore, t.ProviderId,
                    string.Equals(t.ProviderId, provider.ProviderId, StringComparison.Ordinal),
                    t.CreatedAtUtc))
            ],
            MissingPoses = evaluation.MissingPoses,
            PoseSimilarities = similarities,
            Warnings = evaluation.Warnings
        };
    }

    /// <summary>Retire one pose. The person drops back to InProgress if it was required.</summary>
    public async Task DeletePoseAsync(
        string tenantId,
        Guid personId,
        FacePose pose,
        CancellationToken ct = default)
    {
        Person person = await LoadPersonAsync(tenantId, personId, ct);

        FaceTemplate template = await templates.GetActiveForPoseAsync(personId, pose, ct)
            ?? throw DomainException.NotFound("template_not_found", $"No active {pose} capture for this person.");

        template.IsActive = false;
        await unitOfWork.SaveChangesAsync(ct);

        IReadOnlyList<FaceTemplate> active = await templates.GetActiveForPersonAsync(personId, ct);
        person.EnrollmentStatus = Evaluate(active).Status;
        if (person.EnrollmentStatus != EnrollmentStatus.Enrolled)
        {
            person.EnrolledAtUtc = null;
        }

        person.UpdatedAtUtc = clock.UtcNow;
        await unitOfWork.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Erase all biometric data for a person: provider-side templates, stored images and rows.
    /// This is what a privacy-law erasure request actually needs.
    /// </summary>
    public async Task PurgeBiometricsAsync(
        string tenantId,
        Guid personId,
        CancellationToken ct = default)
    {
        Person person = await LoadPersonAsync(tenantId, personId, ct);
        IReadOnlyList<FaceTemplate> all = await templates.GetAllForPersonAsync(personId, ct);

        await provider.DeleteTemplatesAsync([.. all.Select(t => t.TemplateData)], ct);

        foreach (FaceTemplate template in all)
        {
            if (!string.IsNullOrEmpty(template.ImageObjectKey))
            {
                await imageStore.DeleteAsync(template.ImageObjectKey, ct);
            }

            templates.Remove(template);
        }

        person.EnrollmentStatus = EnrollmentStatus.NotEnrolled;
        person.EnrolledAtUtc = null;
        person.UpdatedAtUtc = clock.UtcNow;
        await unitOfWork.SaveChangesAsync(ct);

        logger.LogWarning(
            "Purged {Count} face templates and images for person {PersonId}", all.Count, personId);
    }

    /// <summary>
    /// Create the person and store every pose as one unit. If any capture is rejected the new
    /// person record is removed again, because a person row holding no usable face is worse than
    /// no row at all: it occupies the ExternalId and reads as enrolled in a list.
    /// </summary>
    /// <remarks>
    /// This is compensation, not a database transaction: images already written to the store are
    /// deleted on the way out. A single SQL transaction would not help anyway, since the image
    /// store is a separate system.
    /// </remarks>
    public async Task<(Person Person, IReadOnlyList<CaptureFaceResult> Captures)> RegisterAsync(
        CreatePersonCommand command,
        IReadOnlyList<(FacePose Pose, FaceImage Image)> captures,
        CancellationToken ct = default)
    {
        Person person = await CreatePersonAsync(command, ct);

        try
        {
            IReadOnlyList<CaptureFaceResult> results =
                await CaptureManyAsync(command.TenantId, person.Id, captures, ct);

            return (person, results);
        }
        catch
        {
            try
            {
                await PurgeBiometricsAsync(command.TenantId, person.Id, ct);
                people.Remove(person);
                await unitOfWork.SaveChangesAsync(ct);
            }
            catch (Exception cleanupFailure)
            {
                // Never let cleanup mask the real reason the registration failed.
                logger.LogError(
                    cleanupFailure,
                    "Could not roll back the partial registration for {PersonId}; it may need manual removal",
                    person.Id);
            }

            throw;
        }
    }

    private async Task<Person> LoadPersonAsync(string tenantId, Guid personId, CancellationToken ct)
    {
        Person person = await people.GetByIdAsync(personId, includeTemplates: false, ct)
            ?? throw DomainException.NotFound("person_not_found", $"Person {personId} was not found.");

        if (!string.Equals(person.TenantId, tenantId, StringComparison.Ordinal))
        {
            // Reported as not-found rather than forbidden: confirming the id exists in another
            // tenant would already leak more than the caller is entitled to know.
            throw DomainException.NotFound("person_not_found", $"Person {personId} was not found.");
        }

        return person;
    }

    /// <summary>
    /// Refuse a registration whose face already belongs to a different person id. Without this,
    /// one person can hold two identities and cover for an absent colleague.
    /// </summary>
    private async Task GuardAgainstDuplicateIdentityAsync(
        Person person,
        FaceTemplateMaterial material,
        CancellationToken ct)
    {
        if (!_options.RejectDuplicateEnrollments || material.Kind != FaceTemplateKind.Embedding)
        {
            return;
        }

        IReadOnlyList<FaceCandidate> others = await templates.GetCandidatesAsync(
            person.TenantId, provider.ProviderId, excludePersonId: person.Id, ct);

        if (others.Count == 0)
        {
            return;
        }

        FaceCandidate? conflicting = null;
        double best = double.MinValue;

        foreach (FaceCandidate candidate in others)
        {
            double? score = provider.CompareTemplates(material.Data, candidate.TemplateData);
            if (score.HasValue && score.Value > best)
            {
                best = score.Value;
                conflicting = candidate;
            }
        }

        if (conflicting is not null && best >= _options.DuplicateEnrollmentThreshold)
        {
            logger.LogWarning(
                "Rejected enrollment for {PersonId}: scored {Score:F3} against existing person {OtherId}",
                person.Id, best, conflicting.PersonId);

            throw new FaceProcessingException(
                FaceError.DuplicateIdentity,
                $"This face already matches an existing registration (similarity {best:F2}).")
            {
                Hints =
                [
                    "If this person is genuinely new, an operator should review the existing record.",
                    $"Conflicting person id: {conflicting.PersonId}"
                ]
            };
        }
    }

    private EnrollmentEvaluation Evaluate(IReadOnlyList<FaceTemplate> active)
    {
        HashSet<FacePose> captured = [.. active.Select(t => t.Pose)];
        FacePose[] missing = [.. _options.RequiredPoses.Where(p => !captured.Contains(p))];

        if (missing.Length > 0)
        {
            return new EnrollmentEvaluation(
                active.Count == 0 ? EnrollmentStatus.NotEnrolled : EnrollmentStatus.InProgress,
                missing,
                []);
        }

        // Templates made by a retired model cannot be compared against fresh probes.
        if (active.Any(t => !string.Equals(t.ProviderId, provider.ProviderId, StringComparison.Ordinal)))
        {
            return new EnrollmentEvaluation(
                EnrollmentStatus.Stale,
                [],
                [
                    "Some templates came from another engine or model version. " +
                    $"Re-capture is required for provider {provider.ProviderId}."
                ]);
        }

        List<string> warnings = [];

        if (_options.ValidateEnrollmentConsistency)
        {
            for (int i = 0; i < active.Count; i++)
            {
                for (int j = i + 1; j < active.Count; j++)
                {
                    if (SafeCompare(active[i], active[j]) is { } score &&
                        score < _options.MinEnrollmentPairSimilarity)
                    {
                        warnings.Add(
                            $"{active[i].Pose} and {active[j].Pose} scored {score:F2}, below the " +
                            $"{_options.MinEnrollmentPairSimilarity:F2} floor - they may not be the same person.");
                    }
                }
            }

            if (warnings.Count > 0)
            {
                return new EnrollmentEvaluation(EnrollmentStatus.InProgress, [], warnings);
            }
        }

        return new EnrollmentEvaluation(EnrollmentStatus.Enrolled, [], warnings);
    }

    private double? SafeCompare(FaceTemplate a, FaceTemplate b)
    {
        try
        {
            return provider.CompareTemplates(a.TemplateData, b.TemplateData);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Could not compare templates {A} and {B}", a.Id, b.Id);
            return null;
        }
    }

    private sealed record EnrollmentEvaluation(
        EnrollmentStatus Status,
        IReadOnlyList<FacePose> MissingPoses,
        IReadOnlyList<string> Warnings);
}
