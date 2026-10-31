using System.Text.Json;
using ProcureAi.Audit.Application;
using ProcureAi.Audit.Application.Cdc;
using ProcureAi.Audit.Application.Dtos;
using ProcureAi.Audit.Application.Queries;
using ProcureAi.Audit.Domain;
using Shouldly;

namespace ProcureAi.Audit.Tests;

/// <summary>
/// MongoDB change data capture (plan 06 ruling 16) on real Debezium Server 3.7 output (Fixtures/mongo, captured from the container with
/// infra/debezium/application-mongo.properties). The fixtures are one document's life: the app inserts it (as <c>system</c>), Tom updates
/// it, the app replaces it, someone changes it with mongosh, replaces it with a copy of the old stamp, then Maria deletes it through the
/// app (deleting stamp, then delete). A second supplier is deleted with mongosh, and Tom's conversation is inserted.
/// </summary>
public sealed class MongoChangeTests
{
    private static readonly CdcOptions Cdc = new()
    {
        DefaultKeyColumns = ["id"],
        MaskedCollections = ["assistantchatdb.conversations"],
        MongoStampWindow = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public void Parses_insert_update_replace_delete_events()
    {
        var insert = Parse("supplier-insert");
        insert.Database.ShouldBe("suppliersdb");
        insert.Collection.ShouldBe("suppliers");
        insert.DocumentKey.ShouldBe("acme:contoso-hardware");
        insert.Operation.ShouldBe("c");
        insert.Ord.ShouldBe(1);
        insert.ClusterTime.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1793438613511));
        insert.UpdatedFields.ShouldBeNull();
        var after = insert.After!.Value;
        after.GetProperty("TenantId").GetString().ShouldBe("acme");
        // Extended JSON becomes plain JSON: the decimal price as text, the stamp's date as ISO 8601.
        after.GetProperty("Items")[0].GetProperty("UnitPriceExclVat").GetString().ShouldBe("25200");
        after.GetProperty("_audit").GetProperty("at").GetString().ShouldBe("2026-10-31T09:23:33.4970000+00:00");
        insert.Stamp.ShouldBe(new DocumentStamp("system", "corr-insert", DateTimeOffset.FromUnixTimeMilliseconds(1793438613497), false));

        var update = Parse("supplier-update");
        update.Operation.ShouldBe("u");
        update.UpdatedFields!.Value.GetProperty("Email").GetString().ShouldBe("sales@contoso-hardware.test");
        update.Stamp!.UserId.ShouldBe("tom");

        var replace = Parse("supplier-replace");
        replace.Operation.ShouldBe("u");
        replace.UpdatedFields.ShouldBeNull("a replace has no updateDescription");
        replace.Stamp!.CorrelationId.ShouldBe("corr-replace");

        var outside = Parse("supplier-update-outside");
        outside.RemovedFields.ShouldBe(["VatNumber"]);
        outside.UpdatedFields!.Value.GetProperty("Name").GetString().ShouldBe("Contoso Hardware Bank Change");
        outside.Stamp.ShouldBeNull("the update did not touch _audit, although the document still has the old one");

        var delete = Parse("supplier-delete");
        delete.Operation.ShouldBe("d");
        delete.After.ShouldBeNull();
        delete.DocumentKey.ShouldBe("acme:contoso-hardware", "a delete's key comes from the documentKey header");
        delete.Stamp.ShouldBeNull();

        delete.Id.ShouldBe("suppliersdb:1793438613569.0000000007:suppliers:acme:contoso-hardware");
    }

    [Fact]
    public void Update_with_new_stamp_is_app_with_user()
    {
        var inserted = Parse("supplier-insert").ToDataChange(null, Cdc);
        inserted.Attribution.ShouldBe(new Attribution(AttributionKind.App, "system", "system", null, "corr-insert", null));
        inserted.Operation.ShouldBe("Insert");
        inserted.TenantId.ShouldBe("acme");
        inserted.TransactionId.ShouldBeNull();

        var updated = Parse("supplier-update").ToDataChange(inserted, Cdc);

        updated.Attribution.ShouldBe(new Attribution(AttributionKind.App, "tom", "tom", null, "corr-update", null));
        updated.Operation.ShouldBe("Update");
        updated.Database.ShouldBe("suppliersdb");
        updated.Table.ShouldBe("suppliers");
        updated.PrimaryKey.ShouldBe("acme:contoso-hardware");
        updated.TenantId.ShouldBe("acme");
        updated.CommittedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1793438613516));
        // Old values are the document as the previous change left it, so the log shows old -> new.
        var fields = DataChangeView.Fields(updated.Before, updated.After);
        fields.ShouldContain(new FieldChangeDto("Email", "orders@contoso-hardware.test", "sales@contoso-hardware.test"));
        fields.ShouldNotContain(f => f.Column == "Name");

        var replaced = Parse("supplier-replace").ToDataChange(updated, Cdc);
        replaced.Attribution.UserId.ShouldBe("system");
        replaced.Attribution.Kind.ShouldBe(AttributionKind.App);
    }

    [Fact]
    public void Update_without_stamp_is_outside_app()
    {
        var replaced = Parse("supplier-replace").ToDataChange(null, Cdc);

        var outside = Parse("supplier-update-outside").ToDataChange(replaced, Cdc);

        outside.Attribution.ShouldBe(Attribution.OutsideApp);
        outside.TenantId.ShouldBe("acme");
        var fields = DataChangeView.Fields(outside.Before, outside.After);
        fields.ShouldContain(new FieldChangeDto("Email", "sales@contoso-hardware.test", "pay@attacker.test"));
        fields.ShouldContain(new FieldChangeDto("VatNumber", "4123456789", null));
    }

    [Fact]
    public void Replace_with_a_copy_of_the_old_stamp_is_outside_app()
    {
        var replaced = Parse("supplier-replace").ToDataChange(null, Cdc);
        var outside = Parse("supplier-update-outside").ToDataChange(replaced, Cdc);

        // mongosh read the document and wrote it back: its _audit is the app's last stamp, within the window but not new.
        var copied = Parse("supplier-replace-outside").ToDataChange(outside, Cdc);

        copied.Attribution.ShouldBe(Attribution.OutsideApp);
    }

    [Fact]
    public void Delete_after_the_apps_deleting_stamp_is_app_and_other_deletes_are_outside_app()
    {
        var intent = Parse("supplier-delete-intent").ToDataChange(null, Cdc);
        intent.Attribution.ShouldBe(new Attribution(AttributionKind.App, "maria", "maria", null, "corr-delete", null));
        intent.Stamp!.Deleting.ShouldBeTrue();

        var deleted = Parse("supplier-delete").ToDataChange(intent, Cdc);
        deleted.Attribution.ShouldBe(new Attribution(AttributionKind.App, "maria", "maria", null, "corr-delete", null));
        deleted.Operation.ShouldBe("Delete");
        deleted.TenantId.ShouldBe("acme", "a delete has no document; the tenant is the previous change's");
        deleted.After.ShouldBeNull();
        deleted.Before.ShouldBe(intent.After);

        var unknown = Parse("supplier-delete-outside").ToDataChange(null, Cdc);
        unknown.Attribution.ShouldBe(Attribution.OutsideApp);
        unknown.PrimaryKey.ShouldBe("acme:fabrikam-office");

        var afterAppUpdate = Parse("supplier-delete").ToDataChange(Parse("supplier-update").ToDataChange(null, Cdc), Cdc);
        afterAppUpdate.Attribution.ShouldBe(Attribution.OutsideApp, "the previous stamp was not a deleting one");
    }

    [Fact]
    public void Conversation_values_are_masked()
    {
        var change = Parse("conversation-insert").ToDataChange(null, Cdc);

        change.Database.ShouldBe("assistantchatdb");
        change.Table.ShouldBe("conversations");
        change.TenantId.ShouldBe("acme");
        change.Attribution.ShouldBe(new Attribution(AttributionKind.App, "tom", "tom", null, "corr-chat", null));
        change.After.ShouldNotBeNull();
        change.After.ShouldNotContain("laptop", Case.Insensitive);
        using var after = JsonDocument.Parse(change.After);
        var names = after.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        names.ShouldBe(["_id", "TenantId", "UserId", "Title", "CreatedAt", "UpdatedAt", "Messages", "_audit"]);
        after.RootElement.GetProperty("_id").GetString().ShouldBe("8c0b4b6e-2f2e-4b0e-9a3d-3a5b1f0c9d11");
        foreach (var property in after.RootElement.EnumerateObject().Where(p => p.Name != "_id"))
        {
            property.Value.GetString().ShouldBe("***", property.Name);
        }
    }

    private static MongoChange Parse(string fixture)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mongo");
        var body = File.ReadAllText(Path.Combine(folder, fixture + ".json"));
        var key = File.ReadAllText(Path.Combine(folder, fixture + ".key"));
        return MongoChangeParser.Parse(body, key) ?? throw new InvalidOperationException($"{fixture} did not parse.");
    }
}
