using System.Globalization;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Purchasing.Domain;

/// <summary>A purchase order number such as <c>PO-1001</c>. The tenant sequence <c>order</c> starts at 1, numbers start at 1001.</summary>
public readonly record struct OrderNumber
{
    public const string SequenceName = "order";

    private const string Prefix = "PO-";
    private const int Offset = 1000;

    private OrderNumber(int value) => Value = value;

    /// <summary>The number itself (1001, 1002, ...).</summary>
    public int Value { get; }

    /// <summary>The number for the n-th value (from 1) of the tenant sequence <see cref="SequenceName"/>.</summary>
    public static OrderNumber FromSequence(int sequence) =>
        sequence > 0
            ? new OrderNumber(sequence + Offset)
            : throw new DomainException("An order number sequence must be positive.");

    /// <summary>The number from its stored value (1001, 1002, ...), as persisted.</summary>
    public static OrderNumber FromValue(int value) =>
        value > Offset
            ? new OrderNumber(value)
            : throw new DomainException($"{value} is not a valid order number.");

    public static OrderNumber Parse(string text)
    {
        if (text is not null
            && text.StartsWith(Prefix, StringComparison.Ordinal)
            && text.Length > Prefix.Length
            && text[Prefix.Length..].All(char.IsAsciiDigit)
            && int.TryParse(text[Prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value > Offset)
        {
            return new OrderNumber(value);
        }

        throw new DomainException($"'{text}' is not a valid order number.");
    }

    public override string ToString() => Prefix + Value.ToString(CultureInfo.InvariantCulture);
}
