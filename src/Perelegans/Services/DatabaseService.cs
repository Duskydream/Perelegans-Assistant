using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Perelegans.Data;
using Perelegans.Models;

namespace Perelegans.Services;

public class DatabaseService
{
    public string GetDatabasePath() => PerelegansDbContext.GetDefaultDatabasePath();

    public async Task EnsureDatabaseCreatedAsync()
    {
        await EnsureApplicationUsageSchemaAsync();
        await DropLegacyGameTablesAsync();
    }

    public async Task<List<ApplicationUsage>> GetAllApplicationUsagesAsync()
    {
        await using var db = new PerelegansDbContext();
        return await db.ApplicationUsages
            .AsNoTracking()
            .OrderByDescending(a => a.LastSeenAt)
            .ToListAsync();
    }

    public async Task<List<ApplicationUsageSession>> GetAllApplicationUsageSessionsAsync()
    {
        await using var db = new PerelegansDbContext();
        return await db.ApplicationUsageSessions
            .AsNoTracking()
            .OrderByDescending(s => s.StartTime)
            .ToListAsync();
    }

    public async Task<ApplicationUsage> RecordApplicationUsageSessionAsync(
        string processName,
        string executablePath,
        DateTime startTime,
        DateTime endTime,
        bool isKnownProductivityApp)
    {
        var duration = endTime > startTime ? endTime - startTime : TimeSpan.FromSeconds(1);
        if (duration.TotalSeconds < 1)
        {
            duration = TimeSpan.FromSeconds(1);
        }

        var normalizedProcessName = string.IsNullOrWhiteSpace(processName)
            ? "Unknown"
            : processName.Trim();

        await using var db = new PerelegansDbContext();
        var usage = await db.ApplicationUsages
            .FirstOrDefaultAsync(a => a.ProcessName == normalizedProcessName);

        if (usage == null)
        {
            usage = new ApplicationUsage
            {
                DisplayName = normalizedProcessName,
                ProcessName = normalizedProcessName,
                FirstSeenAt = startTime
            };
            db.ApplicationUsages.Add(usage);
        }

        usage.ExecutablePath = executablePath;
        usage.TotalDuration += duration;
        usage.LastSeenAt = endTime;
        usage.Category = isKnownProductivityApp
            ? ApplicationFocusCategory.Productive
            : usage.Category == ApplicationFocusCategory.Productive
                ? ApplicationFocusCategory.Productive
                : ApplicationFocusCategory.Unknown;

        db.ApplicationUsageSessions.Add(new ApplicationUsageSession
        {
            ApplicationUsage = usage,
            ProcessName = normalizedProcessName,
            ExecutablePath = executablePath,
            StartTime = startTime,
            EndTime = endTime,
            Duration = duration,
            IsKnownProductivityApp = isKnownProductivityApp
        });

        await db.SaveChangesAsync();
        return usage;
    }

    public async Task UpdateApplicationAssessmentAsync(
        string processName,
        bool isProductive,
        string? description,
        string? assistantMessage)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        var normalizedProcessName = processName.Trim();
        await using var db = new PerelegansDbContext();
        var usage = await db.ApplicationUsages
            .FirstOrDefaultAsync(a => a.ProcessName == normalizedProcessName);

        if (usage == null)
        {
            usage = new ApplicationUsage
            {
                DisplayName = normalizedProcessName,
                ProcessName = normalizedProcessName,
                FirstSeenAt = DateTime.Now,
                LastSeenAt = DateTime.Now
            };
            db.ApplicationUsages.Add(usage);
        }

        usage.Category = isProductive
            ? ApplicationFocusCategory.Productive
            : ApplicationFocusCategory.Distracting;
        usage.AiDescription = description;
        usage.LastAssistantMessage = assistantMessage;
        await db.SaveChangesAsync();
    }

    public async Task ClearApplicationUsageDataAsync()
    {
        await using var connection = new SqliteConnection(BuildConnectionString(GetDatabasePath()));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM "ApplicationUsageSessions";
            DELETE FROM "ApplicationUsages";
            VACUUM;
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task BackupDatabaseAsync(string backupPath)
    {
        var directory = Path.GetDirectoryName(backupPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var source = new SqliteConnection(BuildConnectionString(GetDatabasePath()));
        await using var destination = new SqliteConnection(BuildConnectionString(backupPath));

        await source.OpenAsync();
        await destination.OpenAsync();
        source.BackupDatabase(destination);
    }

    public async Task RestoreDatabaseAsync(string backupPath)
    {
        SqliteConnection.ClearAllPools();

        await using var source = new SqliteConnection(BuildConnectionString(backupPath));
        await using var destination = new SqliteConnection(BuildConnectionString(GetDatabasePath()));

        await source.OpenAsync();
        await destination.OpenAsync();
        source.BackupDatabase(destination);

        await destination.CloseAsync();
        await source.CloseAsync();
        SqliteConnection.ClearAllPools();
    }

    private static string BuildConnectionString(string dbPath)
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = dbPath
        }.ToString();
    }

    private async Task EnsureApplicationUsageSchemaAsync()
    {
        await using var connection = new SqliteConnection(BuildConnectionString(GetDatabasePath()));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS "ApplicationUsages" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ApplicationUsages" PRIMARY KEY AUTOINCREMENT,
                "DisplayName" TEXT NOT NULL,
                "ProcessName" TEXT NOT NULL,
                "ExecutablePath" TEXT NOT NULL DEFAULT '',
                "TotalDuration" INTEGER NOT NULL DEFAULT 0,
                "FirstSeenAt" TEXT NOT NULL,
                "LastSeenAt" TEXT NOT NULL,
                "Category" INTEGER NOT NULL DEFAULT 0,
                "AiDescription" TEXT NULL,
                "LastAssistantMessage" TEXT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ApplicationUsages_ProcessName"
            ON "ApplicationUsages" ("ProcessName");

            CREATE TABLE IF NOT EXISTS "ApplicationUsageSessions" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ApplicationUsageSessions" PRIMARY KEY AUTOINCREMENT,
                "ApplicationUsageId" INTEGER NOT NULL,
                "ProcessName" TEXT NOT NULL,
                "ExecutablePath" TEXT NOT NULL DEFAULT '',
                "StartTime" TEXT NOT NULL,
                "EndTime" TEXT NOT NULL,
                "Duration" INTEGER NOT NULL DEFAULT 0,
                "IsKnownProductivityApp" INTEGER NOT NULL DEFAULT 0,
                CONSTRAINT "FK_ApplicationUsageSessions_ApplicationUsages_ApplicationUsageId"
                    FOREIGN KEY ("ApplicationUsageId")
                    REFERENCES "ApplicationUsages" ("Id")
                    ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS "IX_ApplicationUsageSessions_ApplicationUsageId"
            ON "ApplicationUsageSessions" ("ApplicationUsageId");

            CREATE INDEX IF NOT EXISTS "IX_ApplicationUsageSessions_StartTime"
            ON "ApplicationUsageSessions" ("StartTime");
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropLegacyGameTablesAsync()
    {
        await using var connection = new SqliteConnection(BuildConnectionString(GetDatabasePath()));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA foreign_keys = OFF;
            DROP TABLE IF EXISTS "PlaySessions";
            DROP TABLE IF EXISTS "Games";
            DROP TABLE IF EXISTS "RecommendationFeedback";
            PRAGMA foreign_keys = ON;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
