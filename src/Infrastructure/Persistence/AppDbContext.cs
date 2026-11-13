using Application.Abstractions;
using Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Pgvector;

namespace Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<DocumentChunk> Chunks => Set<DocumentChunk>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>
    /// Read by the global query filters. Guid.Empty (no tenant) matches no rows, so a request that
    /// somehow lacks a tenant sees nothing rather than everything.
    /// </summary>
    private Guid CurrentTenantId => tenant.TenantId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasPostgresExtension("vector");

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
            b.Property(x => x.StorageKey).HasMaxLength(200).IsRequired();
            b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.FailureReason).HasMaxLength(500);
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

        modelBuilder.Entity<DocumentChunk>(b =>
        {
            b.ToTable("document_chunks");
            b.HasKey(x => x.Id);
            b.Property(x => x.Content).IsRequired();
            b.Property(x => x.Embedding)
                .HasColumnType($"vector({DocumentChunk.EmbeddingDimensions})")
                .HasConversion(
                    v => new Vector(v),
                    v => v.ToArray(),
                    new ValueComparer<float[]>(
                        (a, c) => a != null && c != null && a.SequenceEqual(c),
                        v => v.Aggregate(0, (h, f) => HashCode.Combine(h, f)),
                        v => v.ToArray()));
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            b.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.DocumentId, x.Index }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.WorkspaceId });
            // Approximate nearest-neighbour index for cosine similarity search.
            b.HasIndex(x => x.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
        });

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(x => x.Id);
            b.Property(x => x.Type).HasMaxLength(100).IsRequired();
            b.Property(x => x.Payload).IsRequired();
            b.Property(x => x.LastError).HasMaxLength(2000);
            // Deliberately no tenant filter: workers are system actors (see OutboxMessage).
            b.HasIndex(x => x.NextAttemptAt).HasFilter("processed_at IS NULL AND dead_at IS NULL");
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
