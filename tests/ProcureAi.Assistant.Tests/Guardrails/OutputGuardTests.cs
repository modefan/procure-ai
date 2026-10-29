using ProcureAi.Assistant.Application.Guardrails;
using Shouldly;

namespace ProcureAi.Assistant.Tests.Guardrails;

/// <summary>The output guard: secrets and personal data in the answer (<see cref="SecretPatterns"/>) and the grounding rule (ruling 10).</summary>
public sealed class OutputGuardTests
{
    [Theory]
    [InlineData("Use Host=db.internal;Port=5432;Password=x to connect.", GuardReasons.ConnectionString)]
    [InlineData("Server=sql01;Database=procure;", GuardReasons.ConnectionString)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEow", GuardReasons.PrivateKey)]
    [InlineData("-----BEGIN PRIVATE KEY-----", GuardReasons.PrivateKey)]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJyaXRhIn0.c2lnbmF0dXJl", GuardReasons.Jwt)]
    [InlineData("sk-abcdefghijklmnopqrstuvwxyz123456", GuardReasons.ApiKey)]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789", GuardReasons.ApiKey)]
    [InlineData("github_pat_11ABCDEFG0123456789_abcdefghijklmnop", GuardReasons.ApiKey)]
    [InlineData("AKIAIOSFODNN7EXAMPLE", GuardReasons.ApiKey)]
    [InlineData("the secret is dGhpcyBpcyBhIHZlcnkgc2VjcmV0IHZhbHVlIGluZGVlZA", GuardReasons.ApiKey)]
    [InlineData("Mail rita.naidoo@acme.co.za for help.", GuardReasons.Email)]
    [InlineData("Call 082 555 1234 today.", GuardReasons.MobileNumber)]
    [InlineData("Call +27 82 555 1234 today.", GuardReasons.MobileNumber)]
    [InlineData("Call +27825551234.", GuardReasons.MobileNumber)]
    public void Secret_patterns_are_found(string text, string reason) => SecretPatterns.Find(text).ShouldBe(reason);

    [Theory]
    [InlineData("Purchases above R 200 000,00 need finance approval [S1].")]
    [InlineData("PR-1043 is approved and order PO-2044 was sent on 2026-10-14.")]
    [InlineData("The total is R 1 250 000,00 incl. VAT, ref 12345.")]
    [InlineData("A token of appreciation under R 500 may be accepted.")]
    [InlineData("Keep the api key policy in mind: keys rotate every 90 days.")]
    [InlineData("Budget IT-200 has R 30 812 345 678,00 left.")]
    public void Ordinary_answers_pass(string text) => SecretPatterns.Find(text).ShouldBeNull();

    [Fact]
    public void Output_with_sa_id_number_is_blocked()
    {
        // 800101 5009 0 8 7: born 1980-01-01, a valid Luhn check digit.
        SecretPatterns.Find("Her ID number is 8001015009087.").ShouldBe(GuardReasons.SaIdNumber);
    }

    [Theory]
    [InlineData("Order 8001015009088 was delivered.")] // the same digits with a wrong check digit
    [InlineData("The amount 1234567890123 is not an ID.")] // 13 digits, no valid Luhn digit
    [InlineData("Reference 80010150090871 (14 digits).")] // longer than an ID number
    public void Thirteen_digit_number_without_a_valid_id_passes(string text) => SecretPatterns.Find(text).ShouldBeNull();

    [Fact]
    public void Rolling_window_catches_a_secret_split_across_updates()
    {
        var guard = new OutputGuard(200);

        guard.Inspect("The admin token is gh").ShouldBeNull();
        guard.Inspect("p_abcdefghijklmnopqrstuvwxyz0123456789 ok").ShouldBe(GuardReasons.ApiKey);
    }

    [Fact]
    public void Rolling_window_forgets_text_older_than_the_window()
    {
        var guard = new OutputGuard(50);

        guard.Inspect("Host=").ShouldBeNull();
        guard.Inspect(new string('a', 60)).ShouldBeNull();
        // "Host=" is out of the window now, so this alone is no connection string.
        guard.Inspect("db;").ShouldBeNull();
    }

    [Theory]
    [InlineData("Purchases above R 200 000 need finance approval [S1].", 2, 0, true)]
    [InlineData("See [S2] and [S1].", 2, 0, true)]
    [InlineData("Purchases need approval.", 2, 0, false)] // no citation
    [InlineData("Purchases need approval [S3].", 2, 0, false)] // a source that does not exist
    [InlineData("Purchases need approval [S0].", 2, 0, false)]
    [InlineData("Purchases need approval [S1].", 0, 0, false)]
    public void Ungrounded_answer_detected(string answer, int sources, int toolResults, bool grounded) =>
        OutputGuard.IsGrounded(answer, sources, toolResults).ShouldBe(grounded);

    [Fact]
    public void Grounded_by_tool_result_only_is_ok() =>
        OutputGuard.IsGrounded("PR-1043 is waiting for finance approval.", sourceCount: 0, toolResultCount: 1).ShouldBeTrue();
}
