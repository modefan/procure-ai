namespace ProcureAi.BuildingBlocks.Web.Auth;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string BearerScheme = "Bearer";
    public const string SmartScheme = "Smart";
    public const string KeyHeader = "X-Api-Key";
    public const string TenantHeader = "X-Tenant-Id";
    public const string CallingServiceHeader = "X-Calling-Service";
    public const string OnBehalfOfUserHeader = "X-On-Behalf-Of-User";
    public const string AdminUserId = "claude-admin";
    public const string AuthMethodClaim = "auth_method";
    public const string AuthMethodApiKey = "api_key";
}
