using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Domain;
using ProcureAi.Assistant.Application.Documents;
using ProcureAi.Assistant.Application.Ingestion;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Concurrency;
using ProcureAi.BuildingBlocks.Web.Paging;
using Wolverine;

namespace ProcureAi.Assistant.Api.Controllers;

/// <summary>
/// The procurement policy documents of the caller's tenant that the assistant answers from (admin: <c>admin.policies</c>). An upload or
/// a replacement is stored at once and ingested in the background (202): extract, guard, chunk, embed; its progress arrives as live
/// updates (<c>policy-document</c>). Files are PDF or Markdown up to <c>Assistant:Uploads:MaxBytes</c> (10 MB, else 413); the type is
/// checked from the content, not only the name (else 415). A document of another tenant is a 404. Replacing and removing need the
/// document's ETag in <c>If-Match</c> (412 when it changed meanwhile, which ingestion progress also does).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/assistant/policy-documents")]
[RequireTenant]
public sealed class PolicyDocumentsController(
    IMessageBus bus, ICacheInvalidator cache, ICurrentUser user, IOptions<AssistantOptions> options, ILogger<PolicyDocumentsController> logger)
    : ControllerBase
{
    private const string What = "The policy document";

    /// <summary>The documents (their newest version and its ingestion status), by title. The ETag is the hash of the body.</summary>
    [HttpGet(Name = "ListPolicyDocuments")]
    [CachedGet(Tags = [AssistantCacheTag.DocumentListTemplate])]
    [RequirePermission(Permissions.AdminPolicies)]
    [ProducesResponseType<PagedResult<PolicyDocumentDto>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<PolicyDocumentDto>>> List([FromQuery] PagingQuery paging, CancellationToken cancellationToken) =>
        await bus.InvokeAsync<PagedResult<PolicyDocumentDto>>(new ListPolicyDocuments(paging.Page, paging.Size()), cancellationToken);

    /// <summary>One document. Its ETag is the concurrency version (<c>rowVersion</c>) to send in <c>If-Match</c>.</summary>
    [HttpGet("{id:guid}", Name = "GetPolicyDocument")]
    [CachedGet(Tags = [AssistantCacheTag.DocumentTemplate])]
    [RequirePermission(Permissions.AdminPolicies)]
    [ProducesResponseType<PolicyDocumentDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PolicyDocumentDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var document = await bus.InvokeAsync<PolicyDocumentDto>(new GetPolicyDocument(id), cancellationToken);
        Response.Headers.ETag = VersionETag.Format(document.RowVersion);
        return document;
    }

    /// <summary>Uploads a new document (version 1, status <c>Uploaded</c>) and queues its ingestion. Without a title the file name is used.</summary>
    [HttpPost(Name = "UploadPolicyDocument")]
    [Idempotent]
    [RequirePermission(Permissions.AdminPolicies)]
    [UploadSizeLimit]
    [TokenCapGate]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<PolicyDocumentDto>(StatusCodes.Status202Accepted, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PolicyDocumentDto>> Upload([FromForm] UploadPolicyDocumentForm form, CancellationToken cancellationToken)
    {
        var (file, title) = (form.File, form.Title);
        var (content, problem) = await ReadAsync(file, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var document = await bus.InvokeAsync<PolicyDocumentDto>(
            new UploadPolicyDocument(title, FileNameOf(file), content!.Value.ContentType, content.Value.Bytes), cancellationToken);
        return await AcceptedAsync(document);
    }

    /// <summary>
    /// Replaces the file: a new version, ingested in the background. The current version stays searchable until the new one is ready;
    /// unchanged sections reuse their embeddings.
    /// </summary>
    [HttpPut("{id:guid}/file", Name = "ReplacePolicyDocumentFile")]
    [RequirePermission(Permissions.AdminPolicies)]
    [RequireIfMatch]
    [UploadSizeLimit]
    [TokenCapGate]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<PolicyDocumentDto>(StatusCodes.Status202Accepted, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<PolicyDocumentDto>> Replace(Guid id, [FromForm] ReplacePolicyDocumentFileForm form, CancellationToken cancellationToken)
    {
        var file = form.File;
        if (Request.ExpectedVersion() is not { } version)
        {
            return IfMatchExtensions.PreconditionFailed("If-Match is not a version tag of this resource.");
        }

        var (content, problem) = await ReadAsync(file, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var document = await bus.InvokeOrConflictAsync<PolicyDocumentDto>(
            new ReplacePolicyDocumentFile(id, version, FileNameOf(file), content!.Value.ContentType, content.Value.Bytes), What, cancellationToken);
        return await AcceptedAsync(document);
    }

    /// <summary>Removes the document (soft delete): it leaves the list and retrieval at once.</summary>
    [HttpDelete("{id:guid}", Name = "RemovePolicyDocument")]
    [RequirePermission(Permissions.AdminPolicies)]
    [RequireIfMatch]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken)
    {
        if (Request.ExpectedVersion() is not { } version)
        {
            return IfMatchExtensions.PreconditionFailed("If-Match is not a version tag of this resource.");
        }

        await bus.InvokeOrConflictAsync(new RemovePolicyDocument(id, version), What, cancellationToken);
        await InvalidateAsync(id);
        return NoContent();
    }

    private async Task<ActionResult<PolicyDocumentDto>> AcceptedAsync(PolicyDocumentDto document)
    {
        await InvalidateAsync(document.Id);
        Response.Headers.ETag = VersionETag.Format(document.RowVersion);
        return Accepted($"/api/v1/assistant/policy-documents/{document.Id}", document);
    }

    /// <summary>The file's bytes and real type, or the problem to answer: 413 over the size limit, 415 when it is not a real PDF or Markdown file.</summary>
    private async Task<((byte[] Bytes, string ContentType)? Content, ObjectResult? Problem)> ReadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var maxBytes = options.Value.Uploads.MaxBytes;
        if (file.Length > maxBytes)
        {
            return (null, ProblemResult(StatusCodes.Status413PayloadTooLarge, $"The file is larger than {maxBytes / (1024 * 1024)} MB."));
        }

        var bytes = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }

        return PolicyFileType.Detect(FileNameOf(file), bytes) is { } contentType
            ? ((bytes, contentType), null)
            : (null, ProblemResult(StatusCodes.Status415UnsupportedMediaType, "Upload a PDF (.pdf) or Markdown (.md) file."));
    }

    /// <summary>The file name without any directory a browser may send.</summary>
    private static string FileNameOf(IFormFile file) => Path.GetFileName(file.FileName.Replace('\\', '/'));

    private static ObjectResult ProblemResult(int status, string title) =>
        new(new ProblemDetails { Status = status, Title = title })
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };

    /// <summary>Drops the cached reads of the document and the tenant's list; a failure is logged, the entries expire on their own.</summary>
    private async Task InvalidateAsync(Guid id)
    {
        try
        {
            await cache.RemoveByTagAsync(AssistantCacheTag.Of(user.TenantId, id), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cache invalidation failed for policy document {DocumentId}; cached reads expire on their own", id);
        }
    }
}

/// <summary>The multipart form of an upload: the file (<c>file</c>) and an optional title (<c>title</c>, else the file name).</summary>
public sealed class UploadPolicyDocumentForm
{
    [Required]
    [FromForm(Name = "file")]
    public IFormFile File { get; set; } = null!;

    [MaxLength(PolicyDocument.TitleMaxLength)]
    [FromForm(Name = "title")]
    public string? Title { get; set; }
}

/// <summary>The multipart form of a replacement: the new file (<c>file</c>).</summary>
public sealed class ReplacePolicyDocumentFileForm
{
    [Required]
    [FromForm(Name = "file")]
    public IFormFile File { get; set; } = null!;
}
