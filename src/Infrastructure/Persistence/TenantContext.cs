using Application.Abstractions;

namespace Infrastructure.Persistence;

/// <summary>Scoped holder for the current tenant; set once per request by the tenant-resolution middleware.</summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; set; }
}
