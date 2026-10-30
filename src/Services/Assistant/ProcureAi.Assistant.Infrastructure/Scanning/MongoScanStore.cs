using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Scanning;
using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Persistence.Mongo;

namespace ProcureAi.Assistant.Infrastructure.Scanning;

/// <summary>
/// The invoice scans in MongoDB (<c>assistantchatdb.invoice_scans</c>). Every call goes through <see cref="TenantCollection{T}"/>, so a scan
/// of another tenant is never found (the internal read sees the tenant of <c>X-Tenant-Id</c>). MongoDB deletes a scan
/// <c>Assistant:Scanning:RetentionDays</c> after it was made (<see cref="ScanRetentionMigration"/>).
/// </summary>
internal sealed class MongoScanStore(TenantCollection<InvoiceScanDocument> scans) : IScanStore
{
    public const string CollectionName = "invoice_scans";

    public Task InsertAsync(InvoiceScan scan, CancellationToken cancellationToken) =>
        scans.InsertOneAsync(InvoiceScanDocument.From(scan), cancellationToken);

    public async Task<InvoiceScan?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var key = id.ToString("D");
        var found = await scans.Find(s => s.Id == key).FirstOrDefaultAsync(cancellationToken);
        return found?.ToDomain();
    }
}

/// <summary>
/// The TTL index on <c>CreatedAt</c>: MongoDB deletes a scan <c>Assistant:Scanning:RetentionDays</c> days after it was made. A changed
/// retention changes the index in place (<c>collMod</c>), since creating it again with another expiry fails.
/// </summary>
internal sealed class ScanRetentionMigration(IOptions<AssistantOptions> options) : IMongoMigration
{
    public const string IndexName = "created_ttl";

    /// <summary>MongoDB's error when an index of that name exists with other options.</summary>
    private const int IndexOptionsConflict = 85;

    public async Task ApplyAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        var expireAfter = TimeSpan.FromDays(options.Value.Scanning.RetentionDays);
        var collection = database.GetCollection<InvoiceScanDocument>(MongoScanStore.CollectionName);
        var model = new CreateIndexModel<InvoiceScanDocument>(
            Builders<InvoiceScanDocument>.IndexKeys.Ascending(s => s.CreatedAt),
            new CreateIndexOptions { Name = IndexName, ExpireAfter = expireAfter });
        try
        {
            await collection.Indexes.CreateOneAsync(model, cancellationToken: cancellationToken);
        }
        catch (MongoCommandException exception) when (exception.Code == IndexOptionsConflict)
        {
            var command = new BsonDocument
            {
                { "collMod", MongoScanStore.CollectionName },
                { "index", new BsonDocument { { "name", IndexName }, { "expireAfterSeconds", (long)expireAfter.TotalSeconds } } },
            };
            await database.RunCommandAsync<BsonDocument>(command, cancellationToken: cancellationToken);
        }
    }
}

/// <summary>
/// How a scan is stored: one document with what the model read, the checks' warnings and who made it. IDs are strings ("D" form), amounts
/// Decimal128 (exact, never through a double), <c>CreatedAt</c> a UTC date (the TTL index needs a date).
/// </summary>
internal sealed class InvoiceScanDocument : ITenantDocument
{
    [BsonId]
    public string Id { get; set; } = null!;

    public string TenantId { get; set; } = null!;

    public string UserId { get; set; } = null!;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    public string Model { get; set; } = "";

    public string Confidence { get; set; } = "";

    public string SupplierName { get; set; } = "";

    public string? SupplierVatNumber { get; set; }

    public string InvoiceNumber { get; set; } = "";

    public string? InvoiceDate { get; set; }

    public string? OrderNumber { get; set; }

    public List<ScannedLineDocument> Lines { get; set; } = [];

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal Vat { get; set; }

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal TotalExclVat { get; set; }

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal TotalInclVat { get; set; }

    public List<ScanWarningDocument> Warnings { get; set; } = [];

    public static InvoiceScanDocument From(InvoiceScan scan) => new()
    {
        Id = scan.Id.ToString("D"),
        TenantId = scan.TenantId,
        UserId = scan.UserId,
        CreatedAt = scan.CreatedAt.UtcDateTime,
        Model = scan.Model,
        Confidence = scan.Confidence,
        SupplierName = scan.Invoice.SupplierName,
        SupplierVatNumber = scan.Invoice.SupplierVatNumber,
        InvoiceNumber = scan.Invoice.InvoiceNumber,
        InvoiceDate = scan.Invoice.InvoiceDate,
        OrderNumber = scan.Invoice.OrderNumber,
        Lines =
        [
            .. scan.Invoice.Lines.Select(l => new ScannedLineDocument
            {
                Description = l.Description,
                Quantity = l.Quantity,
                UnitPriceExclVat = l.UnitPriceExclVat,
                LineTotalExclVat = l.LineTotalExclVat,
            }),
        ],
        Vat = scan.Invoice.Vat,
        TotalExclVat = scan.Invoice.TotalExclVat,
        TotalInclVat = scan.Invoice.TotalInclVat,
        Warnings = [.. scan.Warnings.Select(w => new ScanWarningDocument { Code = w.Code, Message = w.Message })],
    };

    public InvoiceScan ToDomain() => new(
        Guid.Parse(Id), TenantId, UserId, new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)), Model, Confidence,
        new ScannedInvoice(
            SupplierName, SupplierVatNumber, InvoiceNumber, InvoiceDate, OrderNumber,
            [.. Lines.Select(l => new ScannedLine(l.Description, l.Quantity, l.UnitPriceExclVat, l.LineTotalExclVat))],
            Vat, TotalExclVat, TotalInclVat),
        [.. Warnings.Select(w => new ScanWarning(w.Code, w.Message))]);
}

internal sealed class ScannedLineDocument
{
    public string Description { get; set; } = "";

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal Quantity { get; set; }

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal UnitPriceExclVat { get; set; }

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal LineTotalExclVat { get; set; }
}

internal sealed class ScanWarningDocument
{
    public string Code { get; set; } = "";

    public string Message { get; set; } = "";
}
