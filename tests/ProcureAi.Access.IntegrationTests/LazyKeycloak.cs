namespace ProcureAi.Access.IntegrationTests;

/// <summary>
/// A Keycloak for the tests that need one. It starts when the first of them asks (Keycloak is heavy and most Access tests
/// never touch it) and stops with the collection.
/// </summary>
public sealed class LazyKeycloak : IAsyncLifetime
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private KeycloakFixture? _keycloak;

    public async Task<KeycloakFixture> GetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_keycloak is null)
            {
                var keycloak = new KeycloakFixture();
                await keycloak.InitializeAsync();
                _keycloak = keycloak;
            }

            return _keycloak;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_keycloak is not null)
        {
            await _keycloak.DisposeAsync();
        }
    }
}
