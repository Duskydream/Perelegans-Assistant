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

    public async Task<List<FocusTask>> GetFocusTasksAsync()
    {
        await using var db = new PerelegansDbContext();
        return await db.FocusTasks
            .AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<FocusTaskLink>> GetFocusTaskLinksAsync()
    {
        await using var db = new PerelegansDbContext();
        return await db.FocusTaskLinks
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task<FocusTask> CreateFocusTaskAsync(
        string title,
        string originalInput,
        TaskAdventureDraft? adventure)
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(title)
            ? originalInput.Trim()
            : title.Trim();

        await using var db = new PerelegansDbContext();
        var taskCount = await db.FocusTasks.CountAsync();
        var (x, y) = CreateGalaxyCoordinate(taskCount);

        var task = new FocusTask
        {
            Title = normalizedTitle,
            OriginalInput = originalInput.Trim(),
            QuestTitle = string.IsNullOrWhiteSpace(adventure?.QuestTitle)
                ? "主线任务触发"
                : adventure.QuestTitle.Trim(),
            QuestNarrative = string.IsNullOrWhiteSpace(adventure?.QuestNarrative)
                ? $"【主线任务触发】：{normalizedTitle}"
                : adventure.QuestNarrative.Trim(),
            RewardName = string.IsNullOrWhiteSpace(adventure?.RewardName)
                ? "微光徽记"
                : adventure.RewardName.Trim(),
            CreatedAt = DateTime.Now,
            X = x,
            Y = y,
            NodeSize = 18 + Math.Min(12, normalizedTitle.Length / 6d)
        };

        db.FocusTasks.Add(task);
        await db.SaveChangesAsync();

        await LinkRelatedTasksAsync(task);
        return task;
    }

    public async Task<FocusTask?> CompleteLatestActiveFocusTaskAsync(TaskCompletionDraft? completion)
    {
        await using var db = new PerelegansDbContext();
        var task = await db.FocusTasks
            .Where(t => t.Status == FocusTaskStatus.Active)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        if (task == null)
        {
            return null;
        }

        task.Status = FocusTaskStatus.Completed;
        task.CompletedAt = DateTime.Now;
        task.CompletionNarrative = string.IsNullOrWhiteSpace(completion?.CompletionNarrative)
            ? $"战斗胜利！你完成了「{task.Title}」。"
            : completion.CompletionNarrative.Trim();
        task.RewardName = string.IsNullOrWhiteSpace(completion?.RewardName)
            ? task.RewardName
            : completion.RewardName.Trim();
        task.NodeSize = Math.Max(task.NodeSize, 28);

        await db.SaveChangesAsync();
        return task;
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

            CREATE TABLE IF NOT EXISTS "FocusTasks" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_FocusTasks" PRIMARY KEY AUTOINCREMENT,
                "Title" TEXT NOT NULL,
                "OriginalInput" TEXT NOT NULL DEFAULT '',
                "QuestTitle" TEXT NOT NULL DEFAULT '',
                "QuestNarrative" TEXT NOT NULL DEFAULT '',
                "CompletionNarrative" TEXT NOT NULL DEFAULT '',
                "RewardName" TEXT NOT NULL DEFAULT '',
                "Status" INTEGER NOT NULL DEFAULT 0,
                "CreatedAt" TEXT NOT NULL,
                "CompletedAt" TEXT NULL,
                "X" REAL NOT NULL DEFAULT 0,
                "Y" REAL NOT NULL DEFAULT 0,
                "NodeSize" REAL NOT NULL DEFAULT 18
            );

            CREATE INDEX IF NOT EXISTS "IX_FocusTasks_CreatedAt"
            ON "FocusTasks" ("CreatedAt");

            CREATE INDEX IF NOT EXISTS "IX_FocusTasks_Status"
            ON "FocusTasks" ("Status");

            CREATE TABLE IF NOT EXISTS "FocusTaskLinks" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_FocusTaskLinks" PRIMARY KEY AUTOINCREMENT,
                "SourceTaskId" INTEGER NOT NULL,
                "TargetTaskId" INTEGER NOT NULL,
                "Reason" TEXT NOT NULL DEFAULT '',
                "Strength" REAL NOT NULL DEFAULT 0.5
            );

            CREATE INDEX IF NOT EXISTS "IX_FocusTaskLinks_SourceTaskId"
            ON "FocusTaskLinks" ("SourceTaskId");

            CREATE INDEX IF NOT EXISTS "IX_FocusTaskLinks_TargetTaskId"
            ON "FocusTaskLinks" ("TargetTaskId");
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task LinkRelatedTasksAsync(FocusTask newTask)
    {
        await using var db = new PerelegansDbContext();
        var keywords = ExtractKeywords(newTask.Title);
        if (keywords.Count == 0)
        {
            return;
        }

        var candidates = await db.FocusTasks
            .Where(t => t.Id != newTask.Id)
            .OrderByDescending(t => t.CreatedAt)
            .Take(80)
            .ToListAsync();

        foreach (var candidate in candidates)
        {
            var overlap = ExtractKeywords(candidate.Title)
                .Intersect(keywords, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (overlap.Count == 0)
            {
                continue;
            }

            var exists = await db.FocusTaskLinks.AnyAsync(l =>
                (l.SourceTaskId == candidate.Id && l.TargetTaskId == newTask.Id) ||
                (l.SourceTaskId == newTask.Id && l.TargetTaskId == candidate.Id));
            if (exists)
            {
                continue;
            }

            db.FocusTaskLinks.Add(new FocusTaskLink
            {
                SourceTaskId = candidate.Id,
                TargetTaskId = newTask.Id,
                Reason = string.Join(", ", overlap.Take(3)),
                Strength = Math.Clamp(0.35 + overlap.Count * 0.15, 0.35, 0.95)
            });
        }

        await db.SaveChangesAsync();
    }

    private static (double X, double Y) CreateGalaxyCoordinate(int index)
    {
        var angle = index * 2.399963229728653;
        var radius = 38 + Math.Sqrt(index + 1) * 42;
        return (420 + Math.Cos(angle) * radius, 300 + Math.Sin(angle) * radius);
    }

    private static HashSet<string> ExtractKeywords(string text)
    {
        var separators = new[]
        {
            ' ', '\t', '\r', '\n', ',', '.', ';', ':', '，', '。', '；', '：', '、',
            '的', '把', '和', '与', '及', '第', '章'
        };

        return text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 2)
            .Select(token => token.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
