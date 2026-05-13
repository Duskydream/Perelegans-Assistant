using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Perelegans.Models;

namespace Perelegans.Data;

public class PerelegansDbContext : DbContext
{
    public DbSet<ApplicationUsage> ApplicationUsages => Set<ApplicationUsage>();
    public DbSet<ApplicationUsageSession> ApplicationUsageSessions => Set<ApplicationUsageSession>();
    public DbSet<FocusTask> FocusTasks => Set<FocusTask>();
    public DbSet<FocusTaskLink> FocusTaskLinks => Set<FocusTaskLink>();

    private readonly string _dbPath;

    public PerelegansDbContext()
    {
        _dbPath = GetDefaultDatabasePath();
    }

    public PerelegansDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
    }

    public static string GetDefaultDatabasePath()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Perelegans");
        Directory.CreateDirectory(appData);
        return Path.Combine(appData, "perelegans.db");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApplicationUsage>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => a.ProcessName).IsUnique();
            entity.Property(a => a.DisplayName).IsRequired().HasMaxLength(240);
            entity.Property(a => a.ProcessName).IsRequired().HasMaxLength(240);
            entity.Property(a => a.ExecutablePath).HasMaxLength(1000);
            entity.Property(a => a.AiDescription).HasMaxLength(1000);
            entity.Property(a => a.LastAssistantMessage).HasMaxLength(1000);
            entity.Property(a => a.TotalDuration)
                  .HasConversion(
                      v => v.Ticks,
                      v => TimeSpan.FromTicks(v));
            entity.Property(a => a.Category).HasConversion<int>();

            entity.HasMany(a => a.Sessions)
                  .WithOne(s => s.ApplicationUsage)
                  .HasForeignKey(s => s.ApplicationUsageId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApplicationUsageSession>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => s.ApplicationUsageId);
            entity.HasIndex(s => s.StartTime);
            entity.Property(s => s.ProcessName).IsRequired().HasMaxLength(240);
            entity.Property(s => s.ExecutablePath).HasMaxLength(1000);
            entity.Property(s => s.Duration)
                  .HasConversion(
                      v => v.Ticks,
                      v => TimeSpan.FromTicks(v));
        });

        modelBuilder.Entity<FocusTask>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.CreatedAt);
            entity.HasIndex(t => t.Status);
            entity.Property(t => t.Title).IsRequired().HasMaxLength(500);
            entity.Property(t => t.OriginalInput).IsRequired().HasMaxLength(1000);
            entity.Property(t => t.QuestTitle).HasMaxLength(500);
            entity.Property(t => t.QuestNarrative).HasMaxLength(2000);
            entity.Property(t => t.CompletionNarrative).HasMaxLength(2000);
            entity.Property(t => t.RewardName).HasMaxLength(500);
            entity.Property(t => t.Status).HasConversion<int>();
        });

        modelBuilder.Entity<FocusTaskLink>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.HasIndex(l => l.SourceTaskId);
            entity.HasIndex(l => l.TargetTaskId);
            entity.Property(l => l.Reason).HasMaxLength(500);
        });
    }
}
