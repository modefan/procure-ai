using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Application.Abstractions;

public interface IRequestNumberGenerator
{
    /// <summary>The next number for the tenant: sequential per tenant and safe under concurrent callers.</summary>
    Task<RequestNumber> NextAsync(string tenantId, CancellationToken cancellationToken);
}
