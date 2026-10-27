namespace ProcureAi.Access.Application.Abstractions;

/// <summary>
/// The identity provider's side of joining the demo company (Keycloak: the organization and the realm role). Implemented in
/// Infrastructure; Application only says what must be true afterwards.
/// </summary>
public interface IIdentityDirectory
{
    /// <summary>The tenant of the demo company, i.e. the alias of its organization: where self-registered users land.</summary>
    string DemoTenantId { get; }

    /// <summary>
    /// The user as the identity provider holds them, changing nothing. Throws
    /// <see cref="ProcureAi.BuildingBlocks.Application.NotFoundException"/> when the user does not exist.
    /// </summary>
    Task<IdentityProfile> GetProfileAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the user a member of the demo company's organization and gives them <paramref name="role"/>. Safe to repeat: whatever
    /// is already in place stays. Throws <see cref="ProcureAi.BuildingBlocks.Application.NotFoundException"/> when the user does
    /// not exist.
    /// </summary>
    Task EnsureDemoMemberAsync(string userId, string role, CancellationToken cancellationToken);
}

/// <summary>
/// A user's email, display name and organizations (their aliases, i.e. tenant IDs) as the identity provider holds them, not as a
/// caller claims them.
/// </summary>
public sealed record IdentityProfile(string Email, string DisplayName, IReadOnlyList<string> Organizations);
