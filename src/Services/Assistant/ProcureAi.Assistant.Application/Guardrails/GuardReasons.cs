namespace ProcureAi.Assistant.Application.Guardrails;

/// <summary>
/// The reason codes of the guards: short, stable and free of the blocked text, so they are safe as metric tags
/// (<c>assistant.guard_blocks</c>), in logs and in what the user sees (<see cref="Describe"/>).
/// </summary>
public static class GuardReasons
{
    public const string TooLong = "too_long";
    public const string Injection = "injection";
    public const string PromptLeak = "prompt_leak";
    public const string RoleTag = "role_tag";
    public const string EncodedInstructions = "encoded_instructions";
    public const string CrossTenant = "cross_tenant";
    public const string SecretRequest = "secret_request";
    public const string OffTopic = "off_topic";
    public const string ClassifierUnclear = "classifier_unclear";

    /// <summary>A secret, credential or personal data in the model's answer (<see cref="SecretPatterns"/>).</summary>
    public const string ConnectionString = "connection_string";
    public const string PrivateKey = "private_key";
    public const string Jwt = "jwt";
    public const string ApiKey = "api_key";
    public const string Email = "email";
    public const string SaIdNumber = "sa_id_number";
    public const string MobileNumber = "mobile_number";

    /// <summary>A sentence for people (a flagged document's reason, an admin screen). An unknown reason is returned as it is.</summary>
    public static string Describe(string? reason) => reason switch
    {
        null or "" => "The guard flagged this text.",
        TooLong => "The text is longer than the guard allows.",
        Injection => "The text contains instructions to the assistant.",
        PromptLeak => "The text asks for the assistant's instructions.",
        RoleTag => "The text contains chat role or data tags.",
        EncodedInstructions => "The text contains encoded instructions to the assistant.",
        CrossTenant => "The text asks for another organisation's data.",
        SecretRequest => "The text asks for keys, passwords or other secrets.",
        OffTopic => "The question is not about procurement.",
        ClassifierUnclear => "The guard could not classify the text.",
        _ => reason,
    };
}
