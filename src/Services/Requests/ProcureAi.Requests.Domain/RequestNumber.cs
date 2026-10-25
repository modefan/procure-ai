using System.Globalization;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Requests.Domain;

/// <summary>A purchase request number such as <c>PR-0001</c> (4-digit zero padded, wider above 9999).</summary>
public readonly record struct RequestNumber
{
    private const string Prefix = "PR-";

    private RequestNumber(int sequence) => Sequence = sequence;

    public int Sequence { get; }

    public static RequestNumber From(int sequence) =>
        sequence > 0
            ? new RequestNumber(sequence)
            : throw new DomainException("A request number sequence must be positive.");

    public static RequestNumber Parse(string text)
    {
        if (text is not null
            && text.StartsWith(Prefix, StringComparison.Ordinal)
            && text.Length > Prefix.Length
            && text[Prefix.Length..].All(char.IsAsciiDigit)
            && int.TryParse(text[Prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
        {
            return From(sequence);
        }

        throw new DomainException($"'{text}' is not a valid request number.");
    }

    public override string ToString() => Prefix + Sequence.ToString("D4", CultureInfo.InvariantCulture);
}
