namespace ProcureAi.Contracts;

/// <summary>
/// The Keycloak user IDs (<c>sub</c>) of the demo users. The realm import pins them, so seeds in every service and the
/// tests can refer to the same people.
/// </summary>
public static class DemoUsers
{
    public const string Tom = "00000000-0000-4000-8000-000000000001";
    public const string Maria = "00000000-0000-4000-8000-000000000002";
    public const string Dept = "00000000-0000-4000-8000-000000000003";
    public const string Thandi = "00000000-0000-4000-8000-000000000004";
    public const string Admin = "00000000-0000-4000-8000-000000000005";
    public const string Ben = "00000000-0000-4000-8000-000000000006";

    /// <summary>Username to user ID.</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>
    {
        ["tom@acme.test"] = Tom,
        ["maria@acme.test"] = Maria,
        ["dept@acme.test"] = Dept,
        ["thandi@acme.test"] = Thandi,
        ["admin@acme.test"] = Admin,
        ["ben@globex.test"] = Ben,
    };
}
