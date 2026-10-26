using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Approvals.Domain;

/// <summary>Totals strictly above these amounts (incl. VAT) need a department-head / finance step.</summary>
public sealed record ApprovalThresholds(Money DepartmentHeadAbove, Money FinanceAbove);
