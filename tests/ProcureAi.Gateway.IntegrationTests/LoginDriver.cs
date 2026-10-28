using System.Net;
using System.Text.RegularExpressions;

namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>
/// Signs in through the gateway the way a browser does: /bff/login redirects to Keycloak, the login form is posted
/// with the demo credentials, and the redirect back with the code goes to the gateway. Redirects are followed by
/// hand, so each step can be checked. Keycloak has its own cookie jar (the SSO session lives there).
/// </summary>
public sealed partial class LoginDriver : IDisposable
{
    private readonly KeycloakFixture _keycloak;
    private readonly HttpClient _keycloakBrowser;
    private string _lastPage = "";

    public LoginDriver(KeycloakFixture keycloak)
    {
        _keycloak = keycloak;
        _keycloakBrowser = new HttpClient(new CookieJarHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, stripSecure: true));
    }

    /// <summary>A browser follows 301, 302, 303, 307 and 308 alike.</summary>
    public static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>Starts at the gateway; returns the Keycloak URL it redirects to.</summary>
    public static async Task<Uri> StartAsync(HttpClient gateway, string path)
    {
        using var response = await gateway.GetAsync(path);
        IsRedirect(response.StatusCode).ShouldBeTrue();
        return response.Headers.Location!;
    }

    /// <summary>Opens Keycloak's authorize URL. Returns the login form action, or the redirect when SSO skipped the form.</summary>
    public async Task<(string? FormAction, Uri? Redirect)> OpenLoginPageAsync(Uri authorizeUrl)
    {
        // Keycloak answers the authorize URL with a redirect to its login page; a redirect out of Keycloak means
        // its single sign-on session was still alive and the form was skipped.
        var response = await _keycloakBrowser.GetAsync(authorizeUrl);
        for (var hop = 0; hop < 6 && IsRedirect(response.StatusCode); hop++)
        {
            var location = response.Headers.Location!;
            if (location.IsAbsoluteUri && location.Authority != new Uri(_keycloak.BaseUrl).Authority)
            {
                response.Dispose();
                return (null, location);
            }

            var next = location.IsAbsoluteUri ? location : new Uri(new Uri(_keycloak.BaseUrl), location);
            response.Dispose();
            response = await _keycloakBrowser.GetAsync(next);
        }

        using (response)
        {
            _lastPage = await response.Content.ReadAsStringAsync();
            response.StatusCode.ShouldBe(HttpStatusCode.OK, "Keycloak login page: " + PageText(_lastPage));
            return (FindFormAction(_lastPage, "login-actions/authenticate"), null);
        }
    }

    /// <summary>True when the page last opened is Keycloak's registration form.</summary>
    public bool IsRegistrationPage => FindFormAction(_lastPage, "login-actions/registration") is not null;

    /// <summary>Fills in Keycloak's registration form (the page last opened); returns the callback URL it redirects to.</summary>
    public async Task<Uri> RegisterAsync(string email, string password)
    {
        var action = FindFormAction(_lastPage, "login-actions/registration")
                     ?? throw new InvalidOperationException("No registration form on the Keycloak page.");
        using var response = await _keycloakBrowser.PostAsync(action, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["firstName"] = "New",
            ["lastName"] = "Person",
            ["email"] = email,
            ["username"] = email,
            ["password"] = password,
            ["password-confirm"] = password,
        }));
        return await FollowToGatewayAsync(response, "registration");
    }

    /// <summary>
    /// Posts the credentials; returns the callback URL Keycloak redirects to. This realm asks for the username and
    /// then, on a second page, the password; each page is posted with the fields it understands.
    /// </summary>
    public async Task<Uri> SubmitCredentialsAsync(string formAction, string user, string password)
    {
        var action = formAction;
        for (var step = 0; step < 3; step++)
        {
            var response = await _keycloakBrowser.PostAsync(action, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = user,
                ["password"] = password,
                ["credentialId"] = "",
            }));
            if (IsRedirect(response.StatusCode))
            {
                return await FollowToGatewayAsync(response, "login");
            }

            // 200: the next page of the login (the password page). Anything without a form is an error page.
            var html = await response.Content.ReadAsStringAsync();
            response.Dispose();
            action = FindFormAction(html, "login-actions/authenticate")
                     ?? throw new InvalidOperationException("Keycloak did not complete the login: " + PageText(html));
        }

        throw new InvalidOperationException("Keycloak kept asking for credentials; the password is probably wrong.");
    }

    /// <summary>
    /// Follows Keycloak's own redirects (relative or to its host) until one leads out to the gateway callback.
    /// A page instead of a redirect means Keycloak wants something (wrong password, required action): fail with its text.
    /// </summary>
    private async Task<Uri> FollowToGatewayAsync(HttpResponseMessage response, string what)
    {
        for (var hop = 0; hop < 6; hop++)
        {
            if (!IsRedirect(response.StatusCode))
            {
                var html = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"Keycloak did not complete the {what}: {(int)response.StatusCode} {PageText(html)}");
            }

            var location = response.Headers.Location!;
            if (location.IsAbsoluteUri && location.Authority != new Uri(_keycloak.BaseUrl).Authority)
            {
                return location;
            }

            var next = location.IsAbsoluteUri ? location : new Uri(new Uri(_keycloak.BaseUrl), location);
            response.Dispose();
            response = await _keycloakBrowser.GetAsync(next);
        }

        throw new InvalidOperationException($"Keycloak redirected too many times during the {what}.");
    }

    /// <summary>The whole sign-in. Returns where the gateway sent the browser afterwards.</summary>
    /// <summary>The headers of the gateway's answer to the last login callback (the one that issues the session cookie).</summary>
    public Dictionary<string, string>? CallbackHeaders { get; private set; }

    public async Task<Uri> LoginAsync(HttpClient gateway, string user, string path = "/bff/login")
    {
        var authorizeUrl = await StartAsync(gateway, path);
        var (formAction, redirect) = await OpenLoginPageAsync(authorizeUrl);
        var callback = redirect ?? await SubmitCredentialsAsync(
            formAction ?? throw new InvalidOperationException("No login form on Keycloak's page."), user, _keycloak.DemoPassword);
        using var done = await gateway.GetAsync(callback);
        CallbackHeaders = done.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        IsRedirect(done.StatusCode).ShouldBeTrue("Gateway callback failed: " + await done.Content.ReadAsStringAsync());
        return done.Headers.Location!;
    }

    /// <summary>The action of the first form whose action contains <paramref name="actionPart"/> (minimal HTML parsing).</summary>
    public static string? FindFormAction(string html, string actionPart)
    {
        foreach (Match tag in FormTag().Matches(html))
        {
            var action = ActionAttribute().Match(tag.Value);
            if (action.Success)
            {
                var value = WebUtility.HtmlDecode(action.Groups[1].Value);
                if (value.Contains(actionPart, StringComparison.Ordinal))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string PageText(string html)
    {
        var text = Tags().Replace(Scripts().Replace(html, " "), " ");
        text = Whitespace().Replace(text, " ");
        return text[..Math.Min(600, text.Length)];
    }

    [GeneratedRegex("<(script|style)[^>]*>.*?</(script|style)>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Scripts();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\r\n]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("<form\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex FormTag();

    [GeneratedRegex("\\baction\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ActionAttribute();

    public void Dispose() => _keycloakBrowser.Dispose();
}
