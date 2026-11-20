using Microsoft.EntityFrameworkCore;

namespace Backroom.BuildingBlocks.IntegrationTests.Persistence;

/// <summary>A minimal module DbContext over the organization table created by <see cref="NotesSchema"/>.</summary>
public sealed class NotesDbContext(DbContextOptions<NotesDbContext> options) : DbContext(options)
{
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(NotesSchema.Schema);
        modelBuilder.Entity<Note>(note =>
        {
            note.ToTable(NotesSchema.Table);
            note.HasKey(n => n.Id);
            note.Property(n => n.Id).HasColumnName("id");
            note.Property(n => n.OrganizationId).HasColumnName("org_id");
            note.Property(n => n.Text).HasColumnName("text");
        });
    }
}
