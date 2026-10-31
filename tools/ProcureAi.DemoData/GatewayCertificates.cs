namespace ProcureAi.DemoData;

/// <summary>
/// <see cref="DemoDataOptions.AllowUntrustedServerCertificate"/> is for the local AppHost only, whose gateway serves the ASP.NET Core
/// development certificate. The admin key (all permissions) must never go to a server whose certificate was not checked, so the switch is
/// refused for any other gateway (options validation) and the certificate check is only ever skipped for a loopback address.
/// </summary>
public static class GatewayCertificates
{
    /// <summary>True for localhost, 127.0.0.0/8 and ::1: the only hosts an unvalidated certificate is accepted for.</summary>
    public static bool AcceptsUntrusted(Uri requestUri) => requestUri.IsLoopback;
}
