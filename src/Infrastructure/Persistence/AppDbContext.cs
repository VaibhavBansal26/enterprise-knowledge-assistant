using Domain;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Document> Documents => Set<Document>();

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
            b.HasQueryFilter(x => !x.IsDeleted);
            b.HasIndex(x => x.Name);
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
            b.HasQueryFilter(x => !x.IsDeleted);
            b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
        });
    }
}
