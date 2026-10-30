using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.Invoicing.Application.Dtos;

namespace ProcureAi.Invoicing.Application.Mapping;

/// <summary>Fills <see cref="PaymentDto.ReleasedByName"/> from Access (cached lookups; a name Access cannot give stays null).</summary>
public static class PaymentNames
{
    public static async Task<InvoiceDto> WithNamesAsync(
        this InvoiceDto invoice, IAccessDirectory access, string tenantId, CancellationToken cancellationToken)
    {
        var names = await LookupAsync(access, invoice.Payments.Select(p => p.ReleasedBy), tenantId, cancellationToken);
        return invoice.WithNames(names);
    }

    public static InvoiceDto WithNames(this InvoiceDto invoice, IReadOnlyDictionary<string, string?> names) =>
        invoice.Payments.Count == 0
            ? invoice
            : invoice with { Payments = [.. invoice.Payments.Select(p => p with { ReleasedByName = names.GetValueOrDefault(p.ReleasedBy) })] };

    /// <summary>One cached lookup per distinct releaser; the read must not fail because a name could not be looked up.</summary>
    public static async Task<IReadOnlyDictionary<string, string?>> LookupAsync(
        IAccessDirectory access, IEnumerable<string> userIds, string tenantId, CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var userId in userIds.Distinct(StringComparer.Ordinal))
        {
            try
            {
                names[userId] = await access.GetDisplayNameAsync(userId, tenantId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                names[userId] = null;
            }
        }

        return names;
    }
}
