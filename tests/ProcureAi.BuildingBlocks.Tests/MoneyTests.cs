using ProcureAi.BuildingBlocks.Domain;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests;

public class MoneyTests
{
    [Fact]
    public void Vat_of_75600_is_11340()
    {
        Money.Zar(75600m).Vat().Amount.ShouldBe(11340.00m);
        Money.Zar(75600m).InclVat().Amount.ShouldBe(86940.00m);
    }

    [Fact]
    public void Vat_is_rounded_on_the_total()
    {
        var total = Money.Zar(33.33m) * 3;
        total.Amount.ShouldBe(99.99m);
        total.Vat().Amount.ShouldBe(15.00m);
        total.InclVat().Amount.ShouldBe(114.99m);
    }

    [Fact]
    public void Vat_rounds_midpoints_away_from_zero()
    {
        Money.Zar(0.10m).Vat().Amount.ShouldBe(0.02m); // 0.015
        Money.Zar(-0.10m).Vat().Amount.ShouldBe(-0.02m);
    }

    [Fact]
    public void Display_format_is_south_african()
    {
        Money.Zar(86940m).ToDisplayString().ShouldBe("R 86\u00A0940,00");
        Money.Zar(0.5m).ToDisplayString().ShouldBe("R 0,50");
        Money.Zar(1234567.891m).ToDisplayString().ShouldBe("R 1\u00A0234\u00A0567,89");
        Money.Zar(0.005m).ToDisplayString().ShouldBe("R 0,01");
        Money.Zero.ToDisplayString().ShouldBe("R 0,00");
    }

    [Fact]
    public void Negative_amounts_display_with_a_leading_minus()
    {
        Money.Zar(-5m).ToDisplayString().ShouldBe("-R 5,00");
        Money.Zar(-1234.5m).ToDisplayString().ShouldBe("-R 1\u00A0234,50");
        Money.Zar(-0.001m).ToDisplayString().ShouldBe("R 0,00");
    }

    [Fact]
    public void Adding_different_currencies_throws()
    {
        Should.Throw<DomainException>(() => Money.Zar(1) + new Money(1, "USD"));
        Should.Throw<DomainException>(() => Money.Zar(1) - new Money(1, "USD"));
    }

    [Fact]
    public void Non_zar_money_cannot_be_used_for_vat_or_display()
    {
        var usd = new Money(1, "USD");
        Should.Throw<DomainException>(() => usd.Vat());
        Should.Throw<DomainException>(() => usd.InclVat());
        Should.Throw<DomainException>(() => usd.ToDisplayString());
        Should.Throw<DomainException>(() => usd + usd);
    }

    [Fact]
    public void Negative_amounts_are_allowed_for_subtraction_results()
    {
        (Money.Zar(10) - Money.Zar(15)).Amount.ShouldBe(-5m);
    }

    [Fact]
    public void Zero_and_default_are_zar_zero()
    {
        Money.Zero.ShouldBe(Money.Zar(0));
        default(Money).Currency.ShouldBe("ZAR");
        (default(Money) + Money.Zar(2)).ShouldBe(Money.Zar(2));
    }

    [Fact]
    public void Entity_collects_and_clears_domain_events()
    {
        var e = new TestEntity();
        e.Do();
        e.DomainEvents.Count.ShouldBe(1);
        e.ClearDomainEvents();
        e.DomainEvents.ShouldBeEmpty();
    }

    private sealed class TestEntity : Entity<Guid>
    {
        public void Do() => Raise("happened");
    }
}
