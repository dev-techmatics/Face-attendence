using FaceAttendance.Api.Contracts;
using FaceAttendance.Api.Infrastructure;
using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Common;
using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FaceAttendance.Api.Controllers;

/// <summary>People who can be registered. Administration only; a kiosk key cannot reach these.</summary>
[ApiController]
[Route("api/v1/persons")]
[Authorize(Policy = Policies.Admin)]
[Produces("application/json")]
public sealed class PersonsController(
    EnrollmentService enrollment,
    IPersonRepository people,
    ITenantContext tenant) : ControllerBase
{
    /// <summary>Create a person. Faces are attached separately, or in one call via /registrations.</summary>
    [HttpPost]
    [ProducesResponseType<PersonResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PersonResponse>> Create(
        [FromBody] CreatePersonRequest request,
        CancellationToken ct)
    {
        Person person = await enrollment.CreatePersonAsync(
            new CreatePersonCommand(
                tenant.TenantId,
                request.ExternalId,
                request.FullName,
                request.Email,
                request.Department),
            ct);

        return CreatedAtAction(nameof(GetById), new { id = person.Id }, PersonResponse.From(person));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<PersonResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PersonResponse>> GetById(Guid id, CancellationToken ct)
    {
        Person person = await people.GetByIdAsync(id, includeTemplates: false, ct)
            ?? throw DomainException.NotFound("person_not_found", $"Person {id} was not found.");

        if (person.TenantId != tenant.TenantId)
        {
            throw DomainException.NotFound("person_not_found", $"Person {id} was not found.");
        }

        return PersonResponse.From(person);
    }

    /// <summary>Look a person up by the id your own system uses.</summary>
    [HttpGet("by-external-id/{externalId}")]
    [ProducesResponseType<PersonResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PersonResponse>> GetByExternalId(string externalId, CancellationToken ct)
    {
        Person person = await people.GetByExternalIdAsync(tenant.TenantId, externalId, false, ct)
            ?? throw DomainException.NotFound(
                "person_not_found", $"No person with ExternalId {externalId} in this tenant.");

        return PersonResponse.From(person);
    }

    [HttpGet]
    [ProducesResponseType<PagedResponse<PersonResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<PersonResponse>>> Search(
        [FromQuery] string? query,
        [FromQuery] EnrollmentStatus? enrollmentStatus,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        (IReadOnlyList<Person> items, int total) = await people.SearchAsync(
            tenant.TenantId, query, enrollmentStatus, page, pageSize, ct);

        return new PagedResponse<PersonResponse>(
            [.. items.Select(PersonResponse.From)], page, pageSize, total);
    }

    /// <summary>
    /// Current enrolment state: which poses exist, which are missing, and how similar the
    /// stored poses are to each other.
    /// </summary>
    [HttpGet("{id:guid}/enrollment")]
    [ProducesResponseType<EnrollmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EnrollmentResponse>> GetEnrollment(Guid id, CancellationToken ct) =>
        EnrollmentResponse.From(await enrollment.GetEnrollmentAsync(tenant.TenantId, id, ct));

    /// <summary>
    /// Erase every face template and stored image for this person, including from the cloud
    /// provider when one is in use. This is the endpoint a data-erasure request needs.
    /// </summary>
    [HttpDelete("{id:guid}/biometrics")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PurgeBiometrics(Guid id, CancellationToken ct)
    {
        await enrollment.PurgeBiometricsAsync(tenant.TenantId, id, ct);
        return NoContent();
    }
}
