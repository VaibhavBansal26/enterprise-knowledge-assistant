using Application.Abstractions;
using Domain;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>
    /// Read by the global query filters. Guid.Empty (no tenant) matches no rows, so a request that
    /// somehow lacks a tenant sees nothing rather than everything.
    /// </summary>
    private Guid CurrentTenantId => tenant.TenantId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Workspace>(b =>
        {
            b.ToTable("workspaces");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Description).HasMaxLength(2000);
            // PostgreSQL xmin system column as optimistic concurrency token.
            b.Property(x => x.Version).IsRowVersion();
            b.HasQueryFilter(x => !x.IsDeleted && x.TenantId == CurrentTenantId);
            b.HasIndex(x => new { x.TenantId, x.Name });
        });

        modelBuilder.Entity<Document>(b =>
        {
            b.ToTable("documents");
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(300).IsRequired();
            b.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            b.Property(x => x.ContentType).HasMaxLength(150).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Metadata).HasColumnType("jsonb");
            b.Property(x => x.Version).IsRowVersion();
            b.HasQueryFilter(x => !x.IsDeleted && x.TenantId == CurrentTenantId);
            b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
            b.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<WorkspaceMember>(b =>
        {
            b.ToTable("workspace_members");
            b.HasKey(x => x.Id);
            b.Property(x => x.UserId).HasMaxLength(128).IsRequired();
            b.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.WorkspaceId, x.UserId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.UserId });
        });

        modelBuilder.Entity<AuditLog>(b =>
        {
            b.ToTable("audit_logs");
            b.HasKey(x => x.Id);
            b.Property(x => x.UserId).HasMaxLength(128);
            b.Property(x => x.Action).HasMaxLength(20).IsRequired();
            b.Property(x => x.EntityType).HasMaxLength(50).IsRequired();
            b.Property(x => x.ChangedFields).HasMaxLength(500);
            b.Property(x => x.CorrelationId).HasMaxLength(64);
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            b.HasIndex(x => new { x.TenantId, x.CreatedAt });
            b.HasIndex(x => new { x.EntityType, x.EntityId });
        });
    }
}
