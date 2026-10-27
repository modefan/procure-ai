using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ProcureAi.BuildingBlocks.Web.Webhooks;

/// <summary>Why a webhook signature was accepted or refused.</summary>
public enum WebhookVerification
{
    Valid,
    Missing,
    Malformed,
    Expired,
    BadSignature,
}

/// <summary>
/// The signature of a webhook: header <c>X-Procure-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256(secret, "&lt;t&gt;.&lt;raw body&gt;")&gt;</c>.
/// The timestamp is part of the signed text, so a captured request cannot be replayed with a fresh timestamp.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-Procure-Signature";

    private const string TimestampKey = "t=";
    private const string SignatureKey = "v1=";
    private const int HexSignatureLength = 64; // SHA-256, hex

    /// <summary>The lower-case hex HMAC-SHA256 of the timestamp, a dot, and the raw body bytes.</summary>
    public static string Compute(string secret, long unixSeconds, ReadOnlySpan<byte> body) =>
        Convert.ToHexStringLower(ComputeBytes(secret, unixSeconds, body));

    /// <summary>The header value for a request body: <c>t=...,v1=...</c>.</summary>
    public static string CreateHeader(string secret, long unixSeconds, ReadOnlySpan<byte> body) =>
        string.Create(CultureInfo.InvariantCulture, $"{TimestampKey}{unixSeconds},{SignatureKey}{Compute(secret, unixSeconds, body)}");

    /// <summary>Checks a header against the raw body bytes exactly as received. The signature comparison takes constant time.</summary>
    public static WebhookVerification Verify(
        string secret, string? header, ReadOnlySpan<byte> body, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return WebhookVerification.Missing;
        }

        if (!TryParse(header, out var timestamp, out var signature))
        {
            return WebhookVerification.Malformed;
        }

        // Too old, or too far in the future (a clock that disagrees by more than the tolerance).
        var age = now.ToUnixTimeSeconds() - timestamp;
        if (Math.Abs((double)age) > tolerance.TotalSeconds)
        {
            return WebhookVerification.Expired;
        }

        var expected = ComputeBytes(secret, timestamp, body);
        return CryptographicOperations.FixedTimeEquals(expected, signature)
            ? WebhookVerification.Valid
            : WebhookVerification.BadSignature;
    }

    private static byte[] ComputeBytes(string secret, long unixSeconds, ReadOnlySpan<byte> body)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret));
        hmac.AppendData(Encoding.UTF8.GetBytes(unixSeconds.ToString(CultureInfo.InvariantCulture) + "."));
        hmac.AppendData(body);
        return hmac.GetHashAndReset();
    }

    private static bool TryParse(string header, out long timestamp, out byte[] signature)
    {
        timestamp = 0;
        signature = [];
        string? t = null;
        string? v1 = null;
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith(TimestampKey, StringComparison.Ordinal) && t is null)
            {
                t = part[TimestampKey.Length..];
            }
            else if (part.StartsWith(SignatureKey, StringComparison.Ordinal) && v1 is null)
            {
                v1 = part[SignatureKey.Length..];
            }
        }

        if (t is null || v1 is null || v1.Length != HexSignatureLength
            || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out timestamp))
        {
            return false;
        }

        try
        {
            signature = Convert.FromHexString(v1);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
