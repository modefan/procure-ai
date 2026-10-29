using Microsoft.Extensions.Configuration;

namespace ProcureAi.AppHost;

/// <summary>
/// The Assistant's model provider settings, from the AppHost's configuration (user secrets: <c>AI:Provider</c>, <c>AI:Endpoint</c>,
/// <c>AI:ApiKey</c>, <c>AI:Chat:Model</c>, <c>AI:Embedding:Model</c>, <c>AI:Guard:Model</c>). All optional: a setting that is not
/// configured is not passed, and the API keeps its own default (the Fake provider: no key, no account). No parameter is required, so the
/// AppHost starts without any AI account.
/// </summary>
public static class AiSettings
{
    /// <summary>The settings passed as <c>AI__...</c> when configured (the key separately, as a secret parameter).</summary>
    public static readonly string[] PlainKeys = ["Provider", "Endpoint", "Chat:Model", "Embedding:Model", "Guard:Model"];

    public const string ApiKeyParameter = "ai-api-key";

    public static IResourceBuilder<ProjectResource> WithOptionalAiSettings(
        this IResourceBuilder<ProjectResource> api, IDistributedApplicationBuilder builder)
    {
        var ai = builder.Configuration.GetSection("AI");
        foreach (var key in PlainKeys)
        {
            if (ai[key] is { Length: > 0 } value)
            {
                api.WithEnvironment("AI__" + key.Replace(":", "__", StringComparison.Ordinal), value);
            }
        }

        if (ai["ApiKey"] is { Length: > 0 } apiKey)
        {
            // A secret parameter: masked in the dashboard, and only this resource gets it.
            api.WithEnvironment("AI__ApiKey", builder.AddParameter(ApiKeyParameter, apiKey, secret: true));
        }

        return api;
    }
}
