using FaceAttendance.Api.Contracts;
using FaceAttendance.Api.Infrastructure;
using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Common;
using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;
using FaceAttendance.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FaceAttendance.Api.Controllers;

/// <summary>
/// Face registration: the three-pose capture flow. Every upload for one person carries that
/// same person id, which is what ties the three images to one identity.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = Policies.Admin)]
[Produces("application/json")]
public sealed class RegistrationController(
    EnrollmentService enrollment,
    IPersonRepository people,
    IFaceTemplateRepository templates,
    IFaceImageStore imageStore,
    ITenantContext tenant,
    IOptions<FaceRecognitionOptions> options) : ControllerBase
{
    private readonly FaceRecognitionOptions _options = options.Value;

    /// <summary>
    /// Create the person and store all three poses in one request. This is the endpoint a
    /// capture wizard should call: either the whole registration succeeds, or the caller gets
    /// one clear reason why not, with no half-finished person left behind.
    /// </summary>
    [HttpPost("registrations")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(32 * 1024 * 1024)]
    [ProducesResponseType<RegisterPersonResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RegisterPersonResponse>> Register(
        [FromForm] RegisterPersonRequest request,
        CancellationToken ct)
    {
        List<(FacePose Pose, FaceImage Image)> captures =
        [
            (FacePose.Front, await request.Front.ToFaceImageAsync(_options, ct)),
            (FacePose.Left, await request.Left.ToFaceImageAsync(_options, ct)),
            (FacePose.Right, await request.Right.ToFaceImageAsync(_options, ct))
        ];

        (Person person, IReadOnlyList<CaptureFaceResult> results) = await enrollment.RegisterAsync(
            new CreatePersonCommand(
                tenant.TenantId,
                request.ExternalId,
                request.FullName,
                request.Email,
                request.Department),
            captures,
            ct);

        EnrollmentStatus status = results[^1].EnrollmentStatus;

        return CreatedAtAction(
            nameof(PersonsController.GetById),
            "Persons",
            new { id = person.Id },
            new RegisterPersonResponse(
                PersonResponse.From(person),
                [.. results.Select(CaptureFaceResponse.From)],
                status,
                status == EnrollmentStatus.Enrolled));
    }

    /// <summary>
    /// Store one pose for an existing person. Safe to call again for the same pose: the new
    /// capture supersedes the old one instead of adding a duplicate.
    /// </summary>
    [HttpPost("persons/{personId:int}/faces")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    [ProducesResponseType<CaptureFaceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CaptureFaceResponse>> CaptureFace(
        int personId,
        [FromForm] CaptureFaceRequest request,
        CancellationToken ct)
    {
        FaceImage image = await request.Image.ToFaceImageAsync(_options, ct);

        CaptureFaceResult result = await enrollment.CaptureFaceAsync(
            new CaptureFaceCommand(tenant.TenantId, personId, request.Pose, image), ct);

        return CaptureFaceResponse.From(result);
    }

    /// <summary>Store all three poses for a person who already exists.</summary>
    [HttpPost("persons/{personId:int}/faces/batch")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(32 * 1024 * 1024)]
    [ProducesResponseType<IReadOnlyList<CaptureFaceResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CaptureFaceResponse>>> CaptureAllPoses(
        int personId,
        [FromForm] CaptureAllPosesRequest request,
        CancellationToken ct)
    {
        List<(FacePose, FaceImage)> captures =
        [
            (FacePose.Front, await request.Front.ToFaceImageAsync(_options, ct)),
            (FacePose.Left, await request.Left.ToFaceImageAsync(_options, ct)),
            (FacePose.Right, await request.Right.ToFaceImageAsync(_options, ct))
        ];

        IReadOnlyList<CaptureFaceResult> results =
            await enrollment.CaptureManyAsync(tenant.TenantId, personId, captures, ct);

        return Ok(results.Select(CaptureFaceResponse.From).ToArray());
    }

    /// <summary>Retire one pose. The person stops being enrolled until it is re-captured.</summary>
    [HttpDelete("persons/{personId:int}/faces/{pose}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeletePose(int personId, FacePose pose, CancellationToken ct)
    {
        await enrollment.DeletePoseAsync(tenant.TenantId, personId, pose, ct);
        return NoContent();
    }

    /// <summary>
    /// Download a stored registration image. Admin-only and deliberately not cacheable: this
    /// returns a photograph of a person face.
    /// </summary>
    [HttpGet("persons/{personId:int}/faces/{pose}/image")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPoseImage(int personId, FacePose pose, CancellationToken ct)
    {
        Person person = await people.GetByIdAsync(personId, false, ct)
            ?? throw DomainException.NotFound("person_not_found", $"Person {personId} was not found.");

        if (person.TenantId != tenant.TenantId)
        {
            throw DomainException.NotFound("person_not_found", $"Person {personId} was not found.");
        }

        FaceTemplate template = await templates.GetActiveForPoseAsync(personId, pose, ct)
            ?? throw DomainException.NotFound("template_not_found", $"No active {pose} capture for this person.");

        byte[] content = await imageStore.ReadAsync(template.ImageObjectKey, ct)
            ?? throw DomainException.NotFound(
                "image_missing",
                "The template row exists but its image is gone from the store.");

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";

        return File(content, template.ImageContentType);
    }
}
