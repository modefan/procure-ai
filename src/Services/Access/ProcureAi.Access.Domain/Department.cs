using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Access.Domain;

public sealed class Department
{
    private Department()
    {
    }

    public string TenantId { get; private set; } = "";

    public string Code { get; private set; } = "";

    public string Name { get; private set; } = "";

    public string? HeadUserId { get; private set; }

    public static Department Create(string tenantId, string code, string name, string? headUserId = null)
    {
        Require(tenantId, "Tenant");
        Require(code, "Code");
        Require(name, "Name");
        return new Department { TenantId = tenantId, Code = code.Trim(), Name = name.Trim(), HeadUserId = headUserId };
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{name} is required.");
        }
    }
}
