using Microsoft.EntityFrameworkCore;

namespace DAL.Persistence;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<MigrationInfo> Migrations => Set<MigrationInfo>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MigrationInfo>(b =>
        {
            b.ToTable("migrations");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).IsRequired();
            b.Property(x => x.AppliedAtUtc).IsRequired();
        });
    }
}

public sealed class MigrationInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime AppliedAtUtc { get; set; }
}
