namespace ProcureAi.Assistant.Application.Tools;

/// <summary>Why a lookup of another service failed, as far as the tools tell the user.</summary>
public enum ToolFailure
{
    /// <summary>401: the relayed credential was not accepted (an expired token, for example).</summary>
    Unauthorized,

    /// <summary>403: the user may not see it.</summary>
    Forbidden,

    /// <summary>404: there is nothing (that the user may see) by that key.</summary>
    NotFound,

    /// <summary>The service did not answer usefully: unreachable, 5xx or 429, a client timeout, an open circuit.</summary>
    Unavailable,
}

/// <summary>
/// A lookup of another service (<see cref="IRequestsLookup"/>, <see cref="IApprovalsLookup"/>, <see cref="IBudgetsLookup"/>,
/// <see cref="ISuppliersLookup"/>) failed. The tools turn it into an error result (<see cref="ToolResult.Error"/>).
/// </summary>
public sealed class ToolLookupException(ToolFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public ToolFailure Failure { get; } = failure;
}
