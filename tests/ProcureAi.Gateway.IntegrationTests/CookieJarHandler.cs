using System.Net;

namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>
/// A browser's cookie jar for a test client. <paramref name="stripSecure"/> is for Keycloak in the container: it marks its
/// cookies <c>Secure</c> even over plain http on localhost (browsers send them there, .NET's container would not).
/// </summary>
public sealed class CookieJarHandler(HttpMessageHandler inner, bool stripSecure) : DelegatingHandler(inner)
{
    private readonly CookieContainer _jar = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (!uri.IsAbsoluteUri)
        {
            uri = new Uri(GatewayHost.BaseAddress, uri);
        }

        var cookies = _jar.GetCookieHeader(uri);
        if (cookies.Length > 0)
        {
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var setCookie in setCookies)
            {
                _jar.SetCookies(uri, stripSecure ? RemoveSecure(setCookie) : setCookie);
            }
        }

        return response;
    }

    private static string RemoveSecure(string setCookie) =>
        string.Join(';', setCookie.Split(';').Where(part => !part.Trim().Equals("Secure", StringComparison.OrdinalIgnoreCase)));
}
