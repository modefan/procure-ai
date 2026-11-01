using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ProcureAi.Audit.Domain;

namespace ProcureAi.Audit.Application.Cdc;

/// <summary>
/// A MongoDB change as Debezium Server's MongoDB connector publishes it to the <c>cdc</c> exchange (routing key
/// <c>mongo.&lt;database&gt;.&lt;collection&gt;</c>, JSON without schemas). Unlike the Postgres events, the document is a string of MongoDB
/// extended JSON (<c>after</c>, and <c>updateDescription.updatedFields</c>), and a delete carries no document: its key comes in the AMQP
/// header <see cref="DocumentKeyHeader"/> (a transform in infra/debezium/application-mongo.properties copies it there).
/// </summary>
public sealed record DebeziumMongoMessage(
    [property: JsonPropertyName("op")] string? Operation,
    [property: JsonPropertyName("after")] string? After,
    [property: JsonPropertyName("updateDescription")] DebeziumMongoUpdate? UpdateDescription,
    [property: JsonPropertyName("source")] DebeziumMongoSource? Source)
{
    /// <summary>The AMQP header with the document's <c>_id</c> as JSON (itself JSON-encoded: <c>"\"acme:contoso-hardware\""</c>).</summary>
    public const string DocumentKeyHeader = "documentKey";
}

/// <summary>What an update changed: the new values (extended JSON, dotted paths for nested fields) and the removed field names.</summary>
public sealed record DebeziumMongoUpdate(
    [property: JsonPropertyName("updatedFields")] string? UpdatedFields,
    [property: JsonPropertyName("removedFields")] string[]? RemovedFields);

/// <summary>Where and when: the database and collection, the change's position within its cluster-time second, and the time.</summary>
public sealed record DebeziumMongoSource(
    [property: JsonPropertyName("db")] string? Database,
    [property: JsonPropertyName("collection")] string? Collection,
    [property: JsonPropertyName("ord")] int? Ord,
    [property: JsonPropertyName("ts_ms")] long? ClusterTimeMs);

/// <summary>
/// One change of a captured MongoDB document (plan 06 ruling 16), parsed by <see cref="MongoChangeParser"/>: the documents are plain
/// JSON here (extended JSON types such as <c>$date</c> and <c>$numberDecimal</c> become strings).
/// </summary>
/// <param name="DocumentKey">The document's <c>_id</c> as text.</param>
/// <param name="Operation"><c>c</c> (insert), <c>u</c> (update or replace), <c>d</c> (delete), <c>r</c> (snapshot read).</param>
/// <param name="After">The whole document after the change (inserts, replaces and updates); null for a delete.</param>
/// <param name="UpdatedFields">An update's new values by (dotted) field path; null for an insert, a replace or a delete.</param>
/// <param name="RemovedFields">An update's removed field paths.</param>
/// <param name="Ord">The change's position within its cluster-time second (<c>source.ord</c>).</param>
/// <param name="ClusterTime">When MongoDB applied the change (<c>source.ts_ms</c>).</param>
public sealed record MongoChange(
    string Database,
    string Collection,
    string DocumentKey,
    string Operation,
    JsonElement? After,
    JsonElement? UpdatedFields,
    IReadOnlyList<string> RemovedFields,
    int Ord,
    DateTimeOffset ClusterTime)
{
    /// <summary>The field a document's tenant is stored in (<c>ITenantDocument.TenantId</c>).</summary>
    public const string TenantField = "TenantId";

    /// <summary>The field the app's stamp is stored in (<c>AuditStamp.ElementName</c>).</summary>
    public const string StampField = "_audit";

    /// <summary>What a masked value is stored as (the EF Core interceptor's mask).</summary>
    public const string MaskedValue = "***";

    private static readonly Dictionary<string, string> Operations = new(StringComparer.Ordinal)
    {
        ["c"] = "Insert",
        ["u"] = "Update",
        ["d"] = "Delete",
        ["r"] = "Snapshot",
    };

    /// <summary><c>{database}:{ts_ms}.{ord}:{collection}:{key}</c>: the change's place in the oplog, so a redelivery stores nothing new.</summary>
    public string Id =>
        $"{Database}:{ClusterTime.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}.{Ord.ToString("D10", CultureInfo.InvariantCulture)}:{Collection}:{DocumentKey}";

    /// <summary><c>&lt;database&gt;.&lt;collection&gt;</c>, as <c>Audit:Cdc:MaskedCollections</c> lists them.</summary>
    public string Namespace => $"{Database}.{Collection}";

    /// <summary>A replace: an update event without an update description (the whole document was written).</summary>
    public bool IsReplace => Operation == "u" && UpdatedFields is null && RemovedFields.Count == 0;

    public static bool IsKnownOperation(string? operation) => operation is not null && Operations.ContainsKey(operation);

    /// <summary>
    /// The stamp this change wrote: an insert's or replace's <c>_audit</c>, or an update's when the update set <c>_audit</c> (or a field
    /// of it). Null when the change carries none (an update that left <c>_audit</c> alone still shows the old one in <see cref="After"/>).
    /// </summary>
    public DocumentStamp? Stamp
    {
        get
        {
            if (Operation == "c" || IsReplace)
            {
                // An insert's or replace's "after" is the document the change wrote (not a lookup).
                return DocumentStampAfter;
            }

            if (Operation != "u" || UpdatedFields is not { ValueKind: JsonValueKind.Object } updated)
            {
                return null;
            }

            // An update's "after" is looked up when Debezium reads the event (change_streams_update_full): by then the document may be
            // gone (the app's deleting stamp read after the delete) or changed again. The app sets the whole _audit object
            // (TenantCollection, AuditStamp.Apply), so the stamp is read from the update itself; only a write of single _audit fields
            // falls back to the lookup.
            if (updated.TryGetProperty(StampField, out var written))
            {
                return StampOf(written);
            }

            return updated.EnumerateObject().Any(f => f.Name.StartsWith(StampField + ".", StringComparison.Ordinal)) ? DocumentStampAfter : null;
        }
    }

    /// <summary>The stamp in <see cref="After"/> (for an update: the looked-up document, which may be later than the change); null when none.</summary>
    public DocumentStamp? DocumentStampAfter =>
        After is { ValueKind: JsonValueKind.Object } after && after.TryGetProperty(StampField, out var stamp) ? StampOf(stamp) : null;

    /// <summary>
    /// The change as Audit stores it, attributed (plan 06 ruling 16): <b>App</b> with the stamp's user when the change wrote a new stamp
    /// (within <see cref="CdcOptions.MongoStampWindow"/> of the change and not the stamp the document already had); a delete is the app's
    /// when the document's previous change was the app's "deleting" stamp, within the same window. Anything else is <b>OutsideApp</b>.
    /// <paramref name="previous"/> is the last change Audit stored for the same document (its values and stamp), or null.
    /// <para>
    /// An update's values are the previous change's document with the update's own fields applied (updatedFields, removedFields), not
    /// the looked-up <see cref="After"/>, which can already hold later changes; the lookup is used only when there is no previous document
    /// (or a field path cannot be applied to it), and when it found nothing either, the updated fields alone are kept.
    /// </para>
    /// Values of <see cref="CdcOptions.MaskedCollections"/> are replaced by <c>***</c> (field names only).
    /// </summary>
    public DataChange ToDataChange(DataChange? previous, CdcOptions cdc)
    {
        ArgumentNullException.ThrowIfNull(cdc);
        var window = cdc.MongoStampWindow;
        var stamp = Stamp;
        Attribution attribution;
        if (Operation == "d")
        {
            attribution = previous is { Attribution.Kind: AttributionKind.App, Stamp: { Deleting: true } deleting } && Near(deleting.At, window)
                ? Attribution.Stamped(deleting)
                : Attribution.OutsideApp;
        }
        else
        {
            var fresh = stamp is not null && Near(stamp.At, window) && stamp != previous?.Stamp;
            attribution = fresh ? Attribution.Stamped(stamp!) : Attribution.OutsideApp;
        }

        var isUpdate = Operation == "u" && !IsReplace;
        var document = Operation switch
        {
            "d" => null,
            _ when isUpdate => Rebuilt(previous?.After) ?? After ?? UpdatedFields,
            _ => After,
        };
        var masked = cdc.IsMasked(Database, Collection);
        return new DataChange(
            Id,
            Database,
            Collection,
            DocumentKey,
            SubjectId: null,
            // The tenant never changes; a masked previous document shows it as ***, so the stored tenant comes first.
            TenantId: isUpdate ? previous?.TenantId ?? TenantOf(After) : TenantOf(After) ?? previous?.TenantId,
            Operations[Operation],
            TransactionId: null,
            ClusterTime,
            Before: Operation == "c" ? null : previous?.After,
            After: document is { } written ? Serialise(written, masked) : null,
            attribution)
        {
            // The document's stamp after the change: the one it wrote, else (an update that left _audit alone) the one it had.
            Stamp = Operation switch
            {
                "d" => null,
                _ when isUpdate => stamp ?? (previous is not null ? previous.Stamp : DocumentStampAfter),
                _ => stamp,
            },
        };
    }

    // The previous document with this update applied; null when there is none or a path does not fit it (then the lookup is used).
    private JsonElement? Rebuilt(string? previousDocument)
    {
        if (previousDocument is null || JsonNode.Parse(previousDocument) is not JsonObject document)
        {
            return null;
        }

        if (UpdatedFields is { ValueKind: JsonValueKind.Object } updated)
        {
            foreach (var field in updated.EnumerateObject())
            {
                if (!SetPath(document, field.Name, JsonNode.Parse(field.Value.GetRawText())))
                {
                    return null;
                }
            }
        }

        foreach (var removed in RemovedFields)
        {
            if (!RemovePath(document, removed))
            {
                return null;
            }
        }

        return JsonDocument.Parse(document.ToJsonString()).RootElement.Clone();
    }

    private static bool SetPath(JsonObject document, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        if (!TryWalk(document, parts[..^1], create: true, out var parent))
        {
            return false;
        }

        switch (parent)
        {
            case JsonObject obj:
                obj[parts[^1]] = value;
                return true;
            case JsonArray array when int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < array.Count:
                array[index] = value;
                return true;
            default:
                return false;
        }
    }

    private static bool RemovePath(JsonObject document, string path)
    {
        var parts = path.Split('.');
        if (!TryWalk(document, parts[..^1], create: false, out var parent))
        {
            return true; // nothing there to remove
        }

        switch (parent)
        {
            case JsonObject obj:
                obj.Remove(parts[^1]);
                return true;
            case JsonArray array when int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < array.Count:
                array[index] = null; // $unset of an array element leaves null
                return true;
            default:
                return false;
        }
    }

    private static bool TryWalk(JsonObject document, string[] parts, bool create, out JsonNode? node)
    {
        node = document;
        foreach (var part in parts)
        {
            switch (node)
            {
                case JsonObject obj when obj[part] is JsonObject or JsonArray:
                    node = obj[part];
                    break;
                case JsonObject obj when create && obj[part] is null:
                    var child = new JsonObject();
                    obj[part] = child;
                    node = child;
                    break;
                case JsonArray array when int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < array.Count:
                    node = array[index];
                    break;
                default:
                    return false;
            }
        }

        return node is JsonObject or JsonArray;
    }

    private bool Near(DateTimeOffset at, TimeSpan window) => (ClusterTime - at).Duration() <= window;

    private static DocumentStamp? StampOf(JsonElement stamp)
    {
        if (stamp.ValueKind != JsonValueKind.Object
            || !stamp.TryGetProperty("userId", out var user) || user.ValueKind != JsonValueKind.String
            || !stamp.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
        {
            return null;
        }

        var deleting = stamp.TryGetProperty("deleting", out var d) && d.ValueKind == JsonValueKind.True;
        return new DocumentStamp(
            user.GetString()!, StringOf(stamp, "correlationId"), when.ToUniversalTime(), deleting, StringOf(stamp, "actorId"), StringOf(stamp, "callingService"));
    }

    private static string? StringOf(JsonElement stamp, string name) =>
        stamp.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? TenantOf(JsonElement? document) =>
        document is { ValueKind: JsonValueKind.Object } row && row.TryGetProperty(TenantField, out var tenant) && tenant.ValueKind == JsonValueKind.String
            ? tenant.GetString()
            : null;

    // Masked: the field names stay, every value but the key is "***" (the same mask as the EF Core interceptor's).
    private static string Serialise(JsonElement document, bool masked)
    {
        if (!masked)
        {
            return document.GetRawText();
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in document.EnumerateObject())
            {
                writer.WriteString(property.Name, property.Name == "_id" ? DebeziumChange.Text(property.Value) : MaskedValue);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
