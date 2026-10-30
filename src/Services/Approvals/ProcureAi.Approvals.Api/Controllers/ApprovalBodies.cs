using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Approvals.Api.Controllers;

/// <summary>Why the approver rejects the request (shown to the requester).</summary>
public sealed record RejectStepBody(
    [Required, StringLength(ApprovalLimits.MaxReasonLength, MinimumLength = ApprovalLimits.MinReasonLength)] string Reason);

/// <summary>An optional note the approver leaves with the approval (shown in the chain and the audit trail).</summary>
public sealed record ApproveStepBody([StringLength(ApprovalLimits.MaxReasonLength)] string? Comment = null);

/// <summary>Input size limits of the API (the domain enforces the business rules).</summary>
public static class ApprovalLimits
{
    public const int MinReasonLength = 3;
    public const int MaxReasonLength = 500;
    public const int MaxCodeLength = 50;
    public const int MaxUserIdLength = 100;
    public const double MinAmount = 0.01;
    public const double MaxAmount = 1_000_000_000_000;
}
