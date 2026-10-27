using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Suppliers.Domain;
using Shouldly;

namespace ProcureAi.Suppliers.Tests;

public sealed class SupplierTests
{
    private static Supplier Contoso() => new(
        "acme", "contoso-hardware", "Contoso Hardware", "4123456789", "orders@contoso.test", true,
        [
            new CatalogItem("LAP-14", "Laptop 14\"", "each", Money.Zar(25_200m), true),
            new CatalogItem("DISC-01", "Discontinued webcam", "each", Money.Zar(990m), false),
        ]);

    [Fact]
    public void Find_is_case_sensitive_and_returns_inactive_items_too()
    {
        var supplier = Contoso();

        supplier.Find("LAP-14").ShouldNotBeNull().UnitPriceExclVat.ShouldBe(Money.Zar(25_200m));
        supplier.Find("lap-14").ShouldBeNull();
        supplier.Find("DISC-01").ShouldNotBeNull().Active.ShouldBeFalse();
        supplier.Find("NOPE").ShouldBeNull();
        supplier.Id.ShouldBe("acme:contoso-hardware");
    }

    [Fact]
    public void CatalogItem_refuses_negative_price_and_non_zar()
    {
        Should.Throw<DomainException>(() => new CatalogItem("X", "x", "each", Money.Zar(-0.01m), true));
        Should.Throw<DomainException>(() => new CatalogItem("X", "x", "each", new Money(10m, "USD"), true));
        new CatalogItem("X", "x", "each", Money.Zar(0m), true).UnitPriceExclVat.Amount.ShouldBe(0m);
    }

    [Fact]
    public void A_catalog_cannot_list_a_sku_twice()
    {
        Should.Throw<DomainException>(() => new Supplier(
            "acme", "s", "S", "", "", true,
            [new CatalogItem("A", "a", "each", Money.Zar(1m), true), new CatalogItem("A", "a2", "each", Money.Zar(2m), true)]));
    }
}
