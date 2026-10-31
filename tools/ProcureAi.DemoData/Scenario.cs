using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ProcureAi.DemoData;

/// <summary>
/// Where a seeded request stops. The order is the order of the flow; everything from <see cref="Ordered"/> on has a purchase order.
/// <see cref="Invoiced"/> is received in full with a matched invoice; <see cref="ClosedShort"/> is received in part, paid for what came and
/// closed short, which completes the request.
/// </summary>
public enum StopAt
{
    Draft,
    Submitted,
    AwaitingDepartmentHead,
    AwaitingFinance,
    Rejected,
    Cancelled,
    Ordered,
    PartiallyReceived,
    Received,
    Invoiced,
    Mismatched,
    PartiallyPaid,
    Completed,
    ClosedShort,
}

/// <summary>How much of the invoice finance pays: everything payable, or half of each line (rounded down).</summary>
public enum PayMode
{
    Full,
    Partial,
}

/// <summary>
/// The demo requests of one tenant (one file in <c>Scenarios/</c>). <see cref="Finance"/> is the user who types in paper invoices and
/// releases payments; every other actor is named on the request. Users are usernames (<c>tom@acme.test</c>), mapped to their Keycloak IDs
/// through <see cref="ProcureAi.Contracts.DemoUsers"/>. The order of <see cref="Requests"/> is the order they are created in.
/// </summary>
public sealed record Scenario(string Tenant, string Finance, IReadOnlyList<ScenarioRequest> Requests);

/// <summary>
/// One seeded request. <see cref="Key"/> names it in logs and in its Idempotency-Keys (<c>demo:&lt;tenant&gt;:&lt;key&gt;:&lt;step&gt;</c>);
/// the seeder finds an existing one by <see cref="Title"/> and <see cref="Requester"/>. <see cref="Approvers"/> approve the approval steps
/// in step order (the last one rejects when <see cref="RejectReason"/> is set). <see cref="Receive"/> is what arrives (default: everything).
/// <see cref="Invoice"/> is a paper invoice finance types in (only for suppliers that send no invoice webhook); without it the supplier's
/// own invoice is awaited.
/// </summary>
public sealed record ScenarioRequest(
    string Key,
    string Title,
    string Requester,
    string CostCenter,
    string Supplier,
    IReadOnlyList<ScenarioLine> Lines,
    StopAt StopAt,
    IReadOnlyList<string>? Approvers = null,
    string? RejectReason = null,
    string? CancelReason = null,
    IReadOnlyList<ScenarioLine>? Receive = null,
    ScenarioInvoice? Invoice = null,
    PayMode? Pay = null,
    string? CloseShortReason = null)
{
    /// <summary>True when the request gets a purchase order.</summary>
    public bool IsOrdered => StopAt >= StopAt.Ordered;
}

public sealed record ScenarioLine(string Sku, int Quantity);

/// <summary>A paper invoice: every order line at the order price plus <see cref="PriceDelta"/> (Rand excl. VAT per unit; a price difference).</summary>
public sealed record ScenarioInvoice(decimal? PriceDelta = null);

/// <summary>Reads and checks the scenario files.</summary>
public static partial class Scenarios
{
    /// <summary>
    /// Suppliers that post PDF invoices instead of sending invoice webhooks: the simulator's <c>Simulators:Supplier:ManualInvoiceSuppliers</c>
    /// (plan 06 ruling 3). Only these get a paper <see cref="ScenarioInvoice"/>; the others invoice by themselves.
    /// </summary>
    public static readonly IReadOnlySet<string> ManualInvoiceSuppliers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fabrikam-office" };

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The scenario files of a folder, in file name order. Throws when one does not parse or breaks a rule of <see cref="Validate"/>.</summary>
    public static IReadOnlyList<Scenario> Load(string directory)
    {
        var files = Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal).ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException($"No scenario files in {directory}.");
        }

        return [.. files.Select(LoadFile)];
    }

    public static Scenario LoadFile(string path)
    {
        var scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), Json)
                       ?? throw new InvalidOperationException($"{path} is empty.");
        var errors = Validate(scenario);
        return errors.Count == 0
            ? scenario
            : throw new InvalidOperationException($"{Path.GetFileName(path)}: {string.Join(" ", errors)}");
    }

    /// <summary>
    /// The rules a scenario must follow before anything is sent: what each stop needs and allows. Whether users, cost centers and SKUs exist
    /// is checked against the seeds by the tests (the tool has no access to the services' seeds).
    /// </summary>
    public static IReadOnlyList<string> Validate(Scenario scenario)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(scenario.Tenant) || string.IsNullOrWhiteSpace(scenario.Finance))
        {
            errors.Add("The tenant and the finance user are required.");
        }

        if (scenario.Requests is not { Count: > 0 })
        {
            errors.Add("A scenario needs at least one request.");
            return errors;
        }

        foreach (var duplicate in scenario.Requests.GroupBy(r => r.Key).Where(g => g.Count() > 1))
        {
            errors.Add($"Key {duplicate.Key} is used more than once.");
        }

        foreach (var duplicate in scenario.Requests.GroupBy(r => (r.Requester, r.Title)).Where(g => g.Count() > 1))
        {
            errors.Add($"{duplicate.Key.Requester} has two requests titled '{duplicate.Key.Title}' (the seeder finds requests by title).");
        }

        foreach (var request in scenario.Requests)
        {
            errors.AddRange(Validate(scenario, request).Select(e => $"{request.Key}: {e}"));
        }

        return errors;
    }

    private static IEnumerable<string> Validate(Scenario scenario, ScenarioRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Key) || !KeyPattern().IsMatch(r.Key))
        {
            yield return "the key must be lowercase letters, digits and dashes.";
        }

        if (string.IsNullOrWhiteSpace(r.Title) || string.IsNullOrWhiteSpace(r.Requester) || string.IsNullOrWhiteSpace(r.CostCenter)
            || string.IsNullOrWhiteSpace(r.Supplier))
        {
            yield return "title, requester, cost center and supplier are required.";
        }

        if (r.Lines is not { Count: > 0 } || r.Lines.Any(l => l.Quantity < 1) || r.Lines.Select(l => l.Sku).Distinct().Count() != r.Lines.Count)
        {
            yield return "needs lines with distinct SKUs and positive quantities.";
            yield break;
        }

        var approvals = r.Approvers ?? [];
        var needsApproval = r.StopAt is StopAt.AwaitingDepartmentHead or StopAt.AwaitingFinance || r.IsOrdered
                            || (r.StopAt == StopAt.Rejected && r.RejectReason is not null);
        if (needsApproval && approvals.Count == 0)
        {
            yield return $"{r.StopAt} needs the approvers of the steps it passes.";
        }

        if (r.StopAt is StopAt.Draft or StopAt.Cancelled && approvals.Count > 0)
        {
            yield return $"{r.StopAt} is never approved.";
        }

        if (r.RejectReason is not null && (r.StopAt != StopAt.Rejected || r.RejectReason.Trim().Length < 3))
        {
            yield return "a reject reason (3 characters or more) belongs to a Rejected request only.";
        }

        if (r.CancelReason is not null && r.StopAt != StopAt.Cancelled)
        {
            yield return "a cancel reason belongs to a Cancelled request only.";
        }

        var receivesInPart = r.StopAt is StopAt.PartiallyReceived or StopAt.ClosedShort;
        if (receivesInPart != r.Receive is not null)
        {
            yield return receivesInPart ? $"{r.StopAt} needs the part that is received." : "only PartiallyReceived and ClosedShort receive in part.";
        }
        else if (r.Receive is not null)
        {
            var ordered = r.Lines.ToDictionary(l => l.Sku, l => l.Quantity);
            if (r.Receive.Any(l => !ordered.TryGetValue(l.Sku, out var q) || l.Quantity < 1 || l.Quantity > q)
                || r.Receive.Sum(l => l.Quantity) >= r.Lines.Sum(l => l.Quantity))
            {
                yield return "the part received must be some of the ordered quantities, not all of them.";
            }
        }

        if ((r.CloseShortReason is not null) != (r.StopAt == StopAt.ClosedShort))
        {
            yield return "a ClosedShort request (and only that) needs a close-short reason.";
        }

        var invoiced = r.StopAt is StopAt.Invoiced or StopAt.Mismatched or StopAt.PartiallyPaid or StopAt.Completed or StopAt.ClosedShort;
        var paperInvoices = ManualInvoiceSuppliers.Contains(r.Supplier);
        if (r.Invoice is not null && !invoiced)
        {
            yield return $"{r.StopAt} is not invoiced.";
        }
        else if (invoiced && paperInvoices && r.Invoice is null)
        {
            yield return $"{r.Supplier} sends no invoice by itself: {r.StopAt} needs a paper invoice.";
        }
        else if (r.Invoice is not null && !paperInvoices)
        {
            yield return $"{r.Supplier} invoices by itself: a paper invoice would invoice the order twice.";
        }

        var priceDifference = r.Invoice?.PriceDelta is { } delta && delta != 0;
        if (priceDifference != (r.StopAt == StopAt.Mismatched))
        {
            yield return "a price difference on the invoice is what makes (only) a Mismatched request.";
        }

        PayMode? pay = r.StopAt switch
        {
            StopAt.PartiallyPaid => PayMode.Partial,
            StopAt.Completed or StopAt.ClosedShort => PayMode.Full,
            _ => null,
        };
        if (r.Pay != pay)
        {
            yield return pay is null ? $"{r.StopAt} is not paid." : $"{r.StopAt} needs pay: {pay}.";
        }

        if (r.Pay == PayMode.Partial && r.Lines.All(l => l.Quantity < 2))
        {
            yield return "a partial payment pays half of a line, so one line needs 2 or more.";
        }

        if (r.Pay is not null && string.Equals(r.Requester, scenario.Finance, StringComparison.OrdinalIgnoreCase))
        {
            yield return "the requester cannot release the payment (separation of duties).";
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KeyPattern();
}
