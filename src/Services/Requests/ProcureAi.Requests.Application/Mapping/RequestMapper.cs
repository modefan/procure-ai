using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Requests;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Requests.Domain;
using Riok.Mapperly.Abstractions;

namespace ProcureAi.Requests.Application.Mapping;

[Mapper]
public static partial class RequestMapper
{
    [MapProperty(nameof(PurchaseRequest.Number), nameof(RequestDto.Number), Use = nameof(NumberText))]
    [MapProperty(nameof(PurchaseRequest.Lines), nameof(RequestDto.Lines), Use = nameof(OrderedLines))]
    [MapProperty("TotalExclVat.Amount", nameof(RequestDto.TotalExclVat))]
    [MapProperty("Vat.Amount", nameof(RequestDto.Vat))]
    [MapProperty("TotalInclVat.Amount", nameof(RequestDto.TotalInclVat))]
    [MapProperty(nameof(PurchaseRequest.TotalExclVat), nameof(RequestDto.TotalExclVatDisplay), Use = nameof(Display))]
    [MapProperty(nameof(PurchaseRequest.Vat), nameof(RequestDto.VatDisplay), Use = nameof(Display))]
    [MapProperty(nameof(PurchaseRequest.TotalInclVat), nameof(RequestDto.TotalInclVatDisplay), Use = nameof(Display))]
    [MapProperty(nameof(PurchaseRequest.SpentInclVat), nameof(RequestDto.SpentInclVat), Use = nameof(Amount))]
    [MapProperty(nameof(PurchaseRequest.SpentInclVat), nameof(RequestDto.SpentInclVatDisplay), Use = nameof(DisplayOrNull))]
    [MapperIgnoreSource(nameof(PurchaseRequest.CompletedAt))]
    [MapperIgnoreSource(nameof(PurchaseRequest.StatusBeforeCancel))]
    [MapperIgnoreSource(nameof(PurchaseRequest.DomainEvents))]
    [MapperIgnoreSource(nameof(PurchaseRequest.TenantId))]
    [MapperIgnoreSource(nameof(PurchaseRequest.RequesterId))]
    [MapperIgnoreSource(nameof(PurchaseRequest.CancelledBy))]
    public static partial RequestDto ToDto(PurchaseRequest request);

    [MapProperty("UnitPriceExclVat.Amount", nameof(RequestLineDto.UnitPriceExclVat))]
    [MapProperty("TotalExclVat.Amount", nameof(RequestLineDto.TotalExclVat))]
    [MapProperty(nameof(RequestLine.UnitPriceExclVat), nameof(RequestLineDto.UnitPriceExclVatDisplay), Use = nameof(Display))]
    [MapProperty(nameof(RequestLine.TotalExclVat), nameof(RequestLineDto.TotalExclVatDisplay), Use = nameof(Display))]
    public static partial RequestLineDto ToLineDto(RequestLine line);

    /// <summary>Domain event to integration event (the Contracts type other services consume).</summary>
    public static partial RequestSubmitted ToIntegrationEvent(PurchaseRequestSubmitted submitted);

    [MapProperty(nameof(PurchaseRequestCancelled.CancelledAt), nameof(RequestCancelled.OccurredAt))]
    public static partial RequestCancelled ToIntegrationEvent(PurchaseRequestCancelled cancelled);

    [MapProperty("UnitPriceExclVat.Amount", nameof(RequestLineItem.UnitPriceExclVat))]
    [MapperIgnoreSource(nameof(RequestLine.TotalExclVat))]
    private static partial RequestLineItem ToLineItem(RequestLine line);

    private static string NumberText(RequestNumber number) => number.ToString();

    private static string Display(Money money) => money.ToDisplayString();

    private static string? DisplayOrNull(Money? money) => money?.ToDisplayString();

    private static decimal? Amount(Money? money) => money?.Amount;

    private static IReadOnlyList<RequestLineDto> OrderedLines(IReadOnlyList<RequestLine> lines) =>
        lines.OrderBy(l => l.LineNo).Select(ToLineDto).ToList();
}
