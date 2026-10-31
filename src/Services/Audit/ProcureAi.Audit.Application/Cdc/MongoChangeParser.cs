using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace ProcureAi.Audit.Application.Cdc;

/// <summary>
/// Reads what Debezium's MongoDB connector publishes (plan 06 ruling 16) into a <see cref="MongoChange"/>: the documents arrive as strings
/// of MongoDB extended JSON and are turned into plain JSON (dates as ISO 8601 strings, decimals, longs and object IDs as strings), so
/// the data change log shows readable values, like the Postgres changes (decimal.handling.mode=string). No MongoDB library is needed:
/// Application stays free of the driver (architecture rule).
/// </summary>
public static class MongoChangeParser
{
    /// <summary>Parses a message body as Debezium published it. Null when it is not a change of one document.</summary>
    public static MongoChange? Parse(string json, string? documentKeyHeader) =>
        Parse(JsonSerializer.Deserialize<DebeziumMongoMessage>(json), documentKeyHeader);

    /// <summary>Null when the message is not a change of one document (unknown operation, no database, collection or key).</summary>
    public static MongoChange? Parse(DebeziumMongoMessage? message, string? documentKeyHeader)
    {
        if (message?.Source is not { Database: { Length: > 0 } database, Collection: { Length: > 0 } collection } source
            || !MongoChange.IsKnownOperation(message.Operation)
            || source.ClusterTimeMs is not { } clusterTimeMs)
        {
            return null;
        }

        var after = Plain(message.After);
        var updated = Plain(message.UpdateDescription?.UpdatedFields);
        var key = KeyFromHeader(documentKeyHeader) ?? KeyOf(after);
        if (key is null)
        {
            return null;
        }

        return new MongoChange(
            database,
            collection,
            key,
            message.Operation!,
            after,
            updated,
            message.UpdateDescription?.RemovedFields ?? [],
            source.Ord ?? 0,
            DateTimeOffset.FromUnixTimeMilliseconds(clusterTimeMs));
    }

    /// <summary>
    /// A string of MongoDB extended JSON (canonical or relaxed) as plain JSON: <c>{"$date": ...}</c> becomes an ISO 8601 string,
    /// <c>$numberDecimal</c>, <c>$numberLong</c>, <c>$numberInt</c>, <c>$numberDouble</c>, <c>$oid</c>, <c>$uuid</c> and <c>$symbol</c> their text,
    /// <c>$binary</c> its base64. Null for null or blank input.
    /// </summary>
    public static JsonElement? Plain(string? extendedJson)
    {
        if (string.IsNullOrWhiteSpace(extendedJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(extendedJson);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WritePlain(writer, document.RootElement);
        }

        using var plain = JsonDocument.Parse(buffer.WrittenMemory);
        return plain.RootElement.Clone();
    }

    /// <summary>A value as text: strings as they are, everything else as JSON (the same rule as <see cref="DebeziumChange.Text(JsonElement)"/>).</summary>
    public static string? Text(JsonElement value) => DebeziumChange.Text(value);

    // The header holds the key's "id" field: the document's _id serialised as extended JSON, itself JSON-encoded ("\"acme:x\"").
    private static string? KeyFromHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        try
        {
            using var outer = JsonDocument.Parse(header);
            var idJson = outer.RootElement.ValueKind == JsonValueKind.String ? outer.RootElement.GetString() : outer.RootElement.GetRawText();
            return Plain(idJson) is { } id ? Text(id) : null;
        }
        catch (JsonException)
        {
            return header;
        }
    }

    private static string? KeyOf(JsonElement? document) =>
        document is { ValueKind: JsonValueKind.Object } row && row.TryGetProperty("_id", out var id) ? Text(id) : null;

    private static void WritePlain(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object when TryWriteExtendedScalar(writer, element):
                return;
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WritePlain(writer, property.Value);
                }

                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WritePlain(writer, item);
                }

                writer.WriteEndArray();
                return;
            default:
                element.WriteTo(writer);
                return;
        }
    }

    // An object with exactly one "$type" member is one extended JSON value.
    private static bool TryWriteExtendedScalar(Utf8JsonWriter writer, JsonElement element)
    {
        using var members = element.EnumerateObject();
        if (!members.MoveNext())
        {
            return false;
        }

        var member = members.Current;
        if (members.MoveNext() && member.Name != "$binary")
        {
            return false;
        }

        switch (member.Name)
        {
            case "$date":
                writer.WriteStringValue(Date(member.Value));
                return true;
            case "$numberDecimal" or "$numberLong" or "$numberInt" or "$numberDouble" or "$oid" or "$uuid" or "$symbol":
                writer.WriteStringValue(Text(member.Value));
                return true;
            case "$binary":
                writer.WriteStringValue(member.Value.ValueKind == JsonValueKind.Object && member.Value.TryGetProperty("base64", out var base64)
                    ? base64.GetString()
                    : Text(member.Value));
                return true;
            default:
                return false;
        }
    }

    private static string? Date(JsonElement value)
    {
        long? milliseconds = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt64(),
            JsonValueKind.Object when value.TryGetProperty("$numberLong", out var number)
                && long.TryParse(number.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
        if (milliseconds is { } ms)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O", CultureInfo.InvariantCulture);
        }

        // Relaxed form: an ISO 8601 string. Normalised the same way, so a stamp read from an update and from a document compare equal.
        return value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : Text(value);
    }
}
