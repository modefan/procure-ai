using System.Text;
using ProcureAi.BuildingBlocks.Web.Webhooks;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Webhooks;

public sealed class WebhookSignatureTests
{
    private const string Secret = "test-secret-0123456789-0123456789";
    private const long Timestamp = 1793088000;
    private const string BodyText = "{\"eventId\":\"e1\"}";

    // Computed once with HMACSHA256 (key: the secret; message: "1793088000." + body) and pinned: the contract with every sender.
    private const string PinnedSignature = "5c35cf357173d4f00678883b16357a5b65064220e89b940d8aedbffe84fca5f7";

    private static readonly byte[] Body = Encoding.UTF8.GetBytes(BodyText);
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(Timestamp);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private static string Header(long t, string? signature = null) => $"t={t},v1={signature ?? PinnedSignature}";

    [Fact]
    public void Compute_matches_the_pinned_vector() =>
        WebhookSignature.Compute(Secret, Timestamp, Body).ShouldBe(PinnedSignature);

    [Fact]
    public void Compute_matches_a_plain_HMACSHA256()
    {
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes($"{Timestamp}.{BodyText}")));

        WebhookSignature.Compute(Secret, Timestamp, Body).ShouldBe(expected);
    }

    [Fact]
    public void CreateHeader_has_the_documented_format() =>
        WebhookSignature.CreateHeader(Secret, Timestamp, Body).ShouldBe($"t={Timestamp},v1={PinnedSignature}");

    public static TheoryData<string?, WebhookVerification> HeaderCases => new()
    {
        { $"t={Timestamp},v1={PinnedSignature}", WebhookVerification.Valid },
        { $" t={Timestamp} , v1={PinnedSignature} ", WebhookVerification.Valid },
        { $"v1={PinnedSignature},t={Timestamp}", WebhookVerification.Valid },
        { null, WebhookVerification.Missing },
        { "", WebhookVerification.Missing },
        { $"v1={PinnedSignature}", WebhookVerification.Malformed },
        { $"t={Timestamp}", WebhookVerification.Malformed },
        { $"t={Timestamp},v1={new string('z', 64)}", WebhookVerification.Malformed },
        { $"t={Timestamp},v1={PinnedSignature[..62]}", WebhookVerification.Malformed },
        { $"t=abc,v1={PinnedSignature}", WebhookVerification.Malformed },
        { $"t=-5,v1={PinnedSignature}", WebhookVerification.Malformed },
        { $"t={Timestamp + 300},v1={PinnedSignature}", WebhookVerification.BadSignature },
        { $"t={Timestamp - 301},v1={PinnedSignature}", WebhookVerification.Expired },
        { $"t={Timestamp + 301},v1={PinnedSignature}", WebhookVerification.Expired },
    };

    [Theory]
    [MemberData(nameof(HeaderCases))]
    public void Verify_classifies_the_header(string? header, WebhookVerification expected) =>
        WebhookSignature.Verify(Secret, header, Body, Now, Tolerance).ShouldBe(expected);

    [Fact]
    public void A_signature_301_seconds_old_is_expired()
    {
        var header = WebhookSignature.CreateHeader(Secret, Timestamp - 301, Body);

        WebhookSignature.Verify(Secret, header, Body, Now, Tolerance).ShouldBe(WebhookVerification.Expired);
    }

    [Fact]
    public void A_signature_301_seconds_in_the_future_is_expired()
    {
        var header = WebhookSignature.CreateHeader(Secret, Timestamp + 301, Body);

        WebhookSignature.Verify(Secret, header, Body, Now, Tolerance).ShouldBe(WebhookVerification.Expired);
    }

    [Fact]
    public void A_signature_exactly_at_the_tolerance_is_valid()
    {
        var header = WebhookSignature.CreateHeader(Secret, Timestamp - 300, Body);

        WebhookSignature.Verify(Secret, header, Body, Now, Tolerance).ShouldBe(WebhookVerification.Valid);
    }

    [Fact]
    public void One_changed_body_byte_is_a_bad_signature()
    {
        var tampered = (byte[])Body.Clone();
        tampered[^2] ^= 1;

        WebhookSignature.Verify(Secret, Header(Timestamp), tampered, Now, Tolerance).ShouldBe(WebhookVerification.BadSignature);
    }

    [Fact]
    public void Another_secret_is_a_bad_signature() =>
        WebhookSignature.Verify("another-secret-0123456789-0123456789", Header(Timestamp), Body, Now, Tolerance)
            .ShouldBe(WebhookVerification.BadSignature);

    [Fact]
    public void A_re_serialised_body_does_not_verify()
    {
        // Same JSON, different bytes (whitespace): the signature covers the bytes as received.
        var spaced = Encoding.UTF8.GetBytes("{ \"eventId\": \"e1\" }");

        WebhookSignature.Verify(Secret, Header(Timestamp), spaced, Now, Tolerance).ShouldBe(WebhookVerification.BadSignature);
    }

    [Fact]
    public void An_old_signature_cannot_be_reused_with_a_fresh_timestamp()
    {
        // The timestamp is part of the signed text.
        var old = WebhookSignature.Compute(Secret, Timestamp - 3600, Body);

        WebhookSignature.Verify(Secret, Header(Timestamp, old), Body, Now, Tolerance).ShouldBe(WebhookVerification.BadSignature);
    }
}
