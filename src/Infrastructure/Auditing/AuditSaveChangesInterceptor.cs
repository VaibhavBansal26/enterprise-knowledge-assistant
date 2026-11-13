using Application.Abstractions;
using Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Infrastructure.Auditing;

/// <summary>
/// Writes an <see cref="AuditLog"/> row for every create/update/delete of an <see cref="IAuditable"/>
/// entity, in the same transaction as the change itself.
/// </summary>
public sealed class AuditSaveChangesInterceptor(ICurrentUser currentUser, ITenantContext tenant) : SaveChangesInterceptor
{
    // Bookkeeping columns are not interesting as "what changed".
    private static readonly HashSet<string> IgnoredProperties =
        new(StringComparer.Ordinal) { nameof(Workspace.Version), nameof(Workspace.IsDeleted), nameof(Workspace.DeletedAt) };

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        AddAuditEntries(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        AddAuditEntries(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void AddAuditEntries(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();

        var audits = context.ChangeTracker.Entries()
            .Where(e => e.Entity is IAuditable && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(ToAudit)
            .OfType<AuditLog>()
            .ToList();

        context.Set<AuditLog>().AddRange(audits);
    }

    private AuditLog? ToAudit(EntityEntry entry)
    {
        var entity = (Entity)entry.Entity;
        var tenantId = (entry.Entity as ITenantOwned)?.TenantId ?? tenant.TenantId;
        if (tenantId is null)
        {
            return null; // cannot attribute the change to a tenant
        }

        var (action, changed) = entry.State switch
        {
            EntityState.Added => ("Created", null),
            EntityState.Deleted => ("Deleted", null),
            _ when entry.Entity is ISoftDeletable { IsDeleted: true } && entry.Property(nameof(ISoftDeletable.IsDeleted)).IsModified
                => ("Deleted", null),
            _ => ("Updated", ChangedFields(entry)),
        };

        return new AuditLog(
            tenantId.Value,
            currentUser.UserId,
            action,
            entry.Metadata.ClrType.Name,
            entity.Id,
            changed,
            currentUser.CorrelationId);
    }

    private static string? ChangedFields(EntityEntry entry)
    {
        var names = entry.Properties
            .Where(p => p.IsModified && !IgnoredProperties.Contains(p.Metadata.Name))
            .Select(p => p.Metadata.Name)
            .Order(StringComparer.Ordinal)
            .ToList();
        return names.Count == 0 ? null : string.Join(',', names);
    }
}
