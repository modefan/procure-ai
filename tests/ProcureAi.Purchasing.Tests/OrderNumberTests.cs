using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Purchasing.Domain;
using Shouldly;

namespace ProcureAi.Purchasing.Tests;

public sealed class OrderNumberTests
{
    [Theory]
    [InlineData(1, "PO-1001")]
    [InlineData(2, "PO-1002")]
    [InlineData(9000, "PO-10000")]
    public void The_sequence_starts_at_1001(int sequence, string expected) =>
        OrderNumber.FromSequence(sequence).ToString().ShouldBe(expected);

    [Fact]
    public void Parse_round_trips()
    {
        var number = OrderNumber.Parse("PO-1043");
        number.Value.ShouldBe(1043);
        number.ToString().ShouldBe("PO-1043");
        number.ShouldBe(OrderNumber.FromSequence(43));
    }

    [Theory]
    [InlineData("")]
    [InlineData("PR-1001")]
    [InlineData("PO-")]
    [InlineData("PO-12a")]
    [InlineData("PO-1000")]
    [InlineData("PO--1001")]
    public void Parse_refuses_bad_text(string text) =>
        Should.Throw<DomainException>(() => OrderNumber.Parse(text));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void A_sequence_must_be_positive(int sequence) =>
        Should.Throw<DomainException>(() => OrderNumber.FromSequence(sequence));
}
