namespace ProcureAi.Assistant.Application.Abstractions;

/// <summary>
/// The caller's permissions from the runtime role-to-permission matrix (Access), for rules inside a flow that an endpoint policy cannot
/// express: e.g. past purchases are request data, so a question searches them only when the caller has <c>requests.read</c>. Fails closed:
/// when Access cannot answer, the caller has nothing beyond what the endpoint already checked.
/// </summary>
public interface ICallerPermissions
{
    ValueTask<bool> HasAsync(string permission, CancellationToken cancellationToken);
}
