using System.Globalization;
using System.Text;

namespace ProcureAi.BuildingBlocks.Domain;

/// <summary>
/// An amount of money. The demo only trades in Rand (ZAR). A different currency can be constructed
/// (so it can be detected), but any arithmetic, VAT or display on it throws <see cref="DomainException"/>.
/// A default(Money) is ZAR zero.
/// </summary>
public readonly record struct Money : IEquatable<Money>
{
    public const string ZarCurrency = "ZAR";

    private readonly string? _currency;

    public Money(decimal amount, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        Amount = amount;
        _currency = currency.ToUpperInvariant();
    }

    public decimal Amount { get; }

    public string Currency => _currency ?? ZarCurrency;

    public bool Equals(Money other) => Amount == other.Amount && Currency == other.Currency;

    public override int GetHashCode() => HashCode.Combine(Amount, Currency);

    public static Money Zero => new(0m, ZarCurrency);

    public static Money Zar(decimal amount) => new(amount, ZarCurrency);

    public static Money operator +(Money left, Money right)
    {
        EnsureZar(left);
        EnsureZar(right);
        return Zar(left.Amount + right.Amount);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureZar(left);
        EnsureZar(right);
        return Zar(left.Amount - right.Amount);
    }

    public static Money operator *(Money money, int quantity)
    {
        EnsureZar(money);
        return Zar(money.Amount * quantity);
    }

    /// <summary>VAT on this amount (treated as the total excl. VAT), rounded to cents, midpoints away from zero.</summary>
    public Money Vat()
    {
        EnsureZar(this);
        return Zar(Math.Round(Amount * VatRate.Standard, 2, MidpointRounding.AwayFromZero));
    }

    public Money InclVat() => this + Vat();

    /// <summary>Formats as <c>R 86 940,00</c> (U+00A0 thousands separator, comma decimals); negatives as <c>-R 5,00</c>.</summary>
    public string ToDisplayString()
    {
        EnsureZar(this);
        var rounded = Math.Round(Amount, 2, MidpointRounding.AwayFromZero);
        var text = Math.Abs(rounded).ToString("F2", CultureInfo.InvariantCulture);
        var dot = text.IndexOf('.');
        var whole = text[..dot];
        var cents = text[(dot + 1)..];

        var sb = new StringBuilder();
        if (rounded < 0)
        {
            sb.Append('-');
        }

        sb.Append("R ");
        for (var i = 0; i < whole.Length; i++)
        {
            if (i > 0 && (whole.Length - i) % 3 == 0)
            {
                sb.Append('\u00A0');
            }

            sb.Append(whole[i]);
        }

        return sb.Append(',').Append(cents).ToString();
    }

    private static void EnsureZar(Money money)
    {
        if (money.Currency != ZarCurrency)
        {
            throw new DomainException($"Only {ZarCurrency} is supported, got {money.Currency}.");
        }
    }
}
