using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Access.Domain;

public sealed class CostCenter
{
    private CostCenter()
    {
    }

    public string TenantId { get; private set; } = "";

    public string Code { get; private set; } = "";

    public string Name { get; private set; } = "";

    public string DepartmentCode { get; private set; } = "";

    public string? ManagerUserId { get; private set; }

    public static CostCenter Create(string tenantId, string code, string name, string departmentCode, string? managerUserId = null)
    {
        Require(tenantId, "Tenant");
        Require(code, "Code");
        Require(name, "Name");
        Require(departmentCode, "Department");
        return new CostCenter
        {
            TenantId = tenantId,
            Code = code.Trim(),
            Name = name.Trim(),
            DepartmentCode = departmentCode,
            ManagerUserId = managerUserId,
        };
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{name} is required.");
        }
    }
}
