using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ProcureAi.Simulators.Webhooks;

/// <summary>
/// Signs a webhook the way the receivers expect: header <c>X-Procure-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256(secret, "&lt;t&gt;.&lt;raw body&gt;")&gt;</c>.
/// Written out here on purpose: the simulators are another company's system and must not share code with the services. A test
/// proves the services' own verifier accepts it.
/// </summary>
internal static class WebhookSigner
{
    public const string HeaderName = "X-Procure-Signature";

    public static string CreateHeader(string secret, long unixSeconds, ReadOnlySpan<byte> body)
    {
        var timestamp = unixSeconds.ToString(CultureInfo.InvariantCulture);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret));
        hmac.AppendData(Encoding.UTF8.GetBytes(timestamp + "."));
        hmac.AppendData(body);
        return $"t={timestamp},v1={Convert.ToHexStringLower(hmac.GetHashAndReset())}";
    }
}
