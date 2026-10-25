using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.BuildingBlocks.Web.Access;

/// <summary>
/// The caller context of a host without messaging (the gateway): it always acts as itself, with nobody on whose behalf it calls,
/// so outgoing calls carry only <c>X-Calling-Service</c> and the tenant the call names.
/// </summary>
public sealed class SystemCallerContextAccessor(ServiceIdentity service) : ICallerContextAccessor
{
    public ICallerContext Current { get; } = new SystemContext(service);

    private sealed class SystemContext(ServiceIdentity service) : ICallerContext
    {
        public string UserId => SystemCaller.Id;
        public string ActorId => SystemCaller.Service(service.Name);
        public string? TenantId => null;
        public string? CallingService => service.Name;
        public CallerKind Kind => CallerKind.System;
    }
}
