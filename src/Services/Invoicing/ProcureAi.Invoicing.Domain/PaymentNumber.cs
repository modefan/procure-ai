using System.Globalization;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Invoicing.Domain;

/// <summary>A payment number such as <c>PAY-0001</c>: the n-th value of the tenant sequence <c>payment</c>.</summary>
public readonly record struct PaymentNumber
{
    public const string SequenceName = "payment";

    private const string Prefix = "PAY-";

    private PaymentNumber(int value) => Value = value;

    public int Value { get; }

    public static PaymentNumber FromSequence(int sequence) =>
        sequence > 0 ? new PaymentNumber(sequence) : throw new DomainException("A payment number sequence must be positive.");

    /// <summary>The number from its stored value, as persisted.</summary>
    public static PaymentNumber FromValue(int value) => FromSequence(value);

    public override string ToString() => Prefix + Value.ToString("D4", CultureInfo.InvariantCulture);
}
