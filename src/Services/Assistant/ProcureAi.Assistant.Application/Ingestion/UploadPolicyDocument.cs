using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.Assistant.Application.Documents;
using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.Contracts.Assistant;
using Wolverine;

namespace ProcureAi.Assistant.Application.Ingestion;

/// <summary>
/// A new policy document of the caller's tenant (version 1). <paramref name="ContentType"/> is what <see cref="PolicyFileType.Detect"/>
/// found; the Api has checked the size. Without a title the file name (without extension) is used.
/// </summary>
public sealed record UploadPolicyDocument(string? Title, string FileName, string ContentType, byte[] Content);

/// <summary>A new file for a document: the next version (the current one stays searchable until it is ready). 412 when the document changed.</summary>
public sealed record ReplacePolicyDocumentFile(Guid Id, uint ExpectedVersion, string FileName, string ContentType, byte[] Content);

/// <summary>Soft-removes a document: it leaves the list and retrieval. 412 when the document changed.</summary>
public sealed record RemovePolicyDocument(Guid Id, uint ExpectedVersion);

/// <summary>
/// Stores the document and its file, and queues the durable ingestion of the version (<see cref="IngestPolicyDocument"/>) with a
/// <c>PolicyDocumentStatusChanged</c> event, all in one transaction (Wolverine's outbox).
/// </summary>
public static class UploadPolicyDocumentHandler
{
    public static async Task<PolicyDocumentDto> Handle(
        UploadPolicyDocument command, IAssistantDbContext db, IMessageBus bus, ICallerContext caller, IAccessDirectory access,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var tenant = PolicyDocumentWrites.Tenant(caller);
        var now = time.GetUtcNow();
        var title = string.IsNullOrWhiteSpace(command.Title) ? Path.GetFileNameWithoutExtension(command.FileName) : command.Title;
        var document = new PolicyDocument(Guid.CreateVersion7(), tenant, title, command.FileName, caller.UserId, now);
        db.PolicyDocuments.Add(document);
        db.PolicyDocumentFiles.Add(PolicyDocumentWrites.File(document, command.ContentType, command.Content, now));
        await db.SaveChangesAsync(cancellationToken);
        await PolicyDocumentWrites.QueueIngestionAsync(bus, document, now);
        return await PolicyDocumentView.ToDtoAsync(document, access, cancellationToken);
    }
}

public static class ReplacePolicyDocumentFileHandler
{
    public static async Task<PolicyDocumentDto> Handle(
        ReplacePolicyDocumentFile command, IAssistantDbContext db, IMessageBus bus, ICallerContext caller, IAccessDirectory access,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var document = await PolicyDocumentWrites.LoadAsync(db, command.Id, command.ExpectedVersion, cancellationToken);
        var now = time.GetUtcNow();
        document.ReplaceFile(command.FileName, caller.UserId, now);
        db.PolicyDocumentFiles.Add(PolicyDocumentWrites.File(document, command.ContentType, command.Content, now));
        await OptimisticConcurrency.SaveOrConflictAsync(db.SaveChangesAsync, PolicyDocumentWrites.What(command.Id), cancellationToken);
        await PolicyDocumentWrites.QueueIngestionAsync(bus, document, now);
        return await PolicyDocumentView.ToDtoAsync(document, access, cancellationToken);
    }
}

public static class RemovePolicyDocumentHandler
{
    /// <summary>The <c>Status</c> of the <c>PolicyDocumentStatusChanged</c> a removal publishes.</summary>
    public const string RemovedStatus = "Removed";

    public static async Task Handle(
        RemovePolicyDocument command, IAssistantDbContext db, IMessageBus bus, TimeProvider time, CancellationToken cancellationToken)
    {
        var document = await PolicyDocumentWrites.LoadAsync(db, command.Id, command.ExpectedVersion, cancellationToken);
        var now = time.GetUtcNow();
        document.Remove(now);
        await OptimisticConcurrency.SaveOrConflictAsync(db.SaveChangesAsync, PolicyDocumentWrites.What(command.Id), cancellationToken);
        await bus.PublishAsync(new PolicyDocumentStatusChanged(document.Id, document.TenantId, document.Version, RemovedStatus, now));
    }
}

internal static class PolicyDocumentWrites
{
    public static string What(Guid id) => $"Policy document {id}";

    public static string Tenant(ICallerContext caller) =>
        string.IsNullOrWhiteSpace(caller.TenantId) ? throw new ForbiddenException("A policy document needs a tenant.") : caller.TenantId;

    /// <summary>The document of the caller's tenant (the context's filter), not removed, at the version the caller last saw.</summary>
    public static async Task<PolicyDocument> LoadAsync(IAssistantDbContext db, Guid id, uint expectedVersion, CancellationToken cancellationToken)
    {
        var document = await db.PolicyDocuments.FirstOrDefaultAsync(d => d.Id == id && !d.Removed, cancellationToken)
            ?? throw new NotFoundException($"Policy document {id} was not found.");
        OptimisticConcurrency.EnsureVersion(What(id), document.RowVersion, expectedVersion);
        return document;
    }

    public static PolicyDocumentFile File(PolicyDocument document, string contentType, byte[] content, DateTimeOffset now) =>
        new(document.Id, document.Version, document.TenantId, contentType, content, Convert.ToHexStringLower(SHA256.HashData(content)), now);

    /// <summary>The ingestion of the newest version (local durable queue) and its status, through the outbox of the handler.</summary>
    public static async Task QueueIngestionAsync(IMessageBus bus, PolicyDocument document, DateTimeOffset now)
    {
        await bus.PublishAsync(new PolicyDocumentStatusChanged(
            document.Id, document.TenantId, document.Version, document.Status.ToString(), now));
        await bus.SendAsync(new IngestPolicyDocument(document.Id, document.Version));
    }
}
