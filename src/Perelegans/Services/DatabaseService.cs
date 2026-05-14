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
        await EnsureFocusTaskSchemaAsync();
        await EnsureContextMemorySchemaAsync();
        await RepairFocusTaskGalaxyLayoutAsync();
        await RepairContextMemoryGalaxyLayoutAsync();
        await RebuildSemanticTaskLinksAsync();
        await DropLegacyGameTablesAsync();
    }

    public async Task<List<ContextMemory>> GetContextMemoriesAsync()
    {
        await using var db = new PerelegansDbContext();
        return await db.ContextMemories
            .AsNoTracking()
            .OrderByDescending(m => m.UpdatedAt)
            .ToListAsync();
    }

    public async Task<List<ContextMemory>> SearchContextMemoriesAsync(string query, int limit = 8)
    {
        await using var db = new PerelegansDbContext();
        var memories = await db.ContextMemories
            .AsNoTracking()
            .OrderByDescending(m => m.Weight)
            .ThenByDescending(m => m.UpdatedAt)
            .Take(200)
            .ToListAsync();

        var terms = CreateSearchTerms(query);
        if (terms.Count == 0)
        {
            return memories.Take(limit).ToList();
        }

        return memories
            .Select(memory => new
            {
                Memory = memory,
                Score = ScoreMemory(memory, terms)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Memory.Weight)
            .ThenByDescending(item => item.Memory.UpdatedAt)
            .Take(limit)
            .Select(item => item.Memory)
            .ToList();
    }

    public async Task<ContextMemory> UpsertContextMemoryAsync(
        string title,
        string content,
        ContextMemoryType type,
        string source,
        string tags,
        double weight,
        int? id = null)
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(title)
            ? CreateMemoryTitle(content)
            : title.Trim();
        var normalizedContent = content.Trim();
        if (string.IsNullOrWhiteSpace(normalizedContent))
        {
            throw new ArgumentException("Memory content cannot be empty.", nameof(content));
        }

        await using var db = new PerelegansDbContext();
        ContextMemory? memory = null;
        if (id.HasValue)
        {
            memory = await db.ContextMemories.FirstOrDefaultAsync(m => m.Id == id.Value);
        }

        if (memory == null)
        {
            var memoryCount = await db.ContextMemories.CountAsync();
            var (x, y) = CreateGalaxyCoordinate(memoryCount, normalizedTitle);
            memory = new ContextMemory
            {
                CreatedAt = DateTime.Now,
                X = x,
                Y = y
            };
            db.ContextMemories.Add(memory);
        }

        memory.Title = normalizedTitle;
        memory.Content = normalizedContent;
        memory.Type = type;
        memory.Source = source.Trim();
        memory.Tags = tags.Trim();
        memory.Weight = Math.Clamp(weight, 0.1, 1.0);
        memory.ConstellationName = CreateMemoryConstellationName(type, memory.Tags, normalizedTitle);
        memory.NodeSize = CreateMemoryNodeSize(memory.Weight, normalizedContent);
        memory.UpdatedAt = DateTime.Now;

        await db.SaveChangesAsync();
        return memory;
    }

    public async Task MarkContextMemoriesUsedAsync(IEnumerable<int> ids)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0)
        {
            return;
        }

        await using var db = new PerelegansDbContext();
        var memories = await db.ContextMemories
            .Where(m => idList.Contains(m.Id))
            .ToListAsync();
        foreach (var memory in memories)
        {
            memory.LastUsedAt = DateTime.Now;
        }

        await db.SaveChangesAsync();
    }

    public async Task<bool> DeleteContextMemoryAsync(int id)
    {
        await using var db = new PerelegansDbContext();
        var memory = await db.ContextMemories.FirstOrDefaultAsync(m => m.Id == id);
        if (memory == null)
        {
            return false;
        }

        db.ContextMemories.Remove(memory);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task UpdateContextMemoryPositionAsync(int id, double x, double y)
    {
        await using var db = new PerelegansDbContext();
        var memory = await db.ContextMemories.FirstOrDefaultAsync(m => m.Id == id);
        if (memory == null)
        {
            return;
        }

        memory.X = Math.Clamp(x, 0, 820);
        memory.Y = Math.Clamp(y, 0, 560);
        await db.SaveChangesAsync();
    }

    public async Task ClearContextMemoriesAsync()
    {
        await using var connection = new SqliteConnection(BuildConnectionString(GetDatabasePath()));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM "ContextMemories";
            VACUUM;
            """;
        await command.ExecuteNonQueryAsync();
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
        var (x, y) = CreateGalaxyCoordinate(taskCount, normalizedTitle);

        var task = new FocusTask
        {
            Title = normalizedTitle,
            OriginalInput = originalInput.Trim(),
            QuestTitle = string.IsNullOrWhiteSpace(adventure?.QuestTitle)
                ? "主线任务触发"
                : adventure.QuestTitle.Trim(),
            QuestNarrative = string.IsNullOrWhiteSpace(adventure?.QuestNarrative)
                ? $"已记录任务：{normalizedTitle}"
                : adventure.QuestNarrative.Trim(),
            RewardName = string.IsNullOrWhiteSpace(adventure?.RewardName)
                ? "完成产出"
                : adventure.RewardName.Trim(),
            AiSummary = NormalizeInsightText(
                adventure?.Summary,
                adventure?.QuestNarrative,
                $"聚焦完成：{normalizedTitle}"),
            NextAction = string.IsNullOrWhiteSpace(adventure?.NextAction)
                ? CreateFallbackNextAction(normalizedTitle)
                : adventure.NextAction.Trim(),
            Difficulty = Math.Clamp(adventure?.Difficulty ?? 2, 1, 5),
            EstimatedMinutes = Math.Clamp(adventure?.EstimatedMinutes ?? 25, 5, 480),
            Tags = NormalizeTags(adventure?.Tags, normalizedTitle),
            ConstellationName = string.IsNullOrWhiteSpace(adventure?.ConstellationName)
                ? CreateFallbackConstellationName(normalizedTitle)
                : adventure.ConstellationName.Trim(),
            CreatedAt = DateTime.Now,
            X = x,
            Y = y,
            NodeSize = CreateNodeSize(normalizedTitle, false)
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
            ? $"已完成「{task.Title}」。"
            : completion.CompletionNarrative.Trim();
        task.RewardName = string.IsNullOrWhiteSpace(completion?.RewardName)
            ? task.RewardName
            : completion.RewardName.Trim();
        task.NodeSize = Math.Max(task.NodeSize, 28);

        await db.SaveChangesAsync();
        return task;
    }

    public async Task<FocusTask?> UpdateFocusTaskAsync(
        int id,
        string title,
        DateTime createdAt,
        bool isCompleted)
    {
        var normalizedTitle = title.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return null;
        }

        await using var db = new PerelegansDbContext();
        var task = await db.FocusTasks.FirstOrDefaultAsync(t => t.Id == id);
        if (task == null)
        {
            return null;
        }

        task.Title = normalizedTitle;
        task.CreatedAt = createdAt;
        task.Status = isCompleted ? FocusTaskStatus.Completed : FocusTaskStatus.Active;
        task.CompletedAt = isCompleted
            ? task.CompletedAt ?? DateTime.Now
            : null;
        task.NodeSize = CreateNodeSize(normalizedTitle, isCompleted);

        await db.FocusTaskLinks
            .Where(l => l.SourceTaskId == id || l.TargetTaskId == id)
            .ExecuteDeleteAsync();
        await db.SaveChangesAsync();

        await LinkRelatedTasksAsync(task);
        return task;
    }

    public async Task UpdateFocusTaskPositionAsync(int id, double x, double y)
    {
        await using var db = new PerelegansDbContext();
        var task = await db.FocusTasks.FirstOrDefaultAsync(t => t.Id == id);
        if (task == null)
        {
            return;
        }

        task.X = Math.Clamp(x, 0, 820);
        task.Y = Math.Clamp(y, 0, 560);
        await db.SaveChangesAsync();
    }

    public async Task<bool> DeleteFocusTaskAsync(int id)
    {
        await using var db = new PerelegansDbContext();
        var task = await db.FocusTasks.FirstOrDefaultAsync(t => t.Id == id);
        if (task == null)
        {
            return false;
        }

        await db.FocusTaskLinks
            .Where(l => l.SourceTaskId == id || l.TargetTaskId == id)
            .ExecuteDeleteAsync();
        db.FocusTasks.Remove(task);
        await db.SaveChangesAsync();
        return true;
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

    private async Task EnsureFocusTaskSchemaAsync()
    {
        await using var connection = new SqliteConnection(BuildConnectionString(GetDatabasePath()));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS "FocusTasks" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_FocusTasks" PRIMARY KEY AUTOINCREMENT,
                "Title" TEXT NOT NULL,
                "OriginalInput" TEXT NOT NULL DEFAULT '',
                "QuestTitle" TEXT NOT NULL DEFAULT '',
                "QuestNarrative" TEXT NOT NULL DEFAULT '',
                "CompletionNarrative" TEXT NOT NULL DEFAULT '',
                "RewardName" TEXT NOT NULL DEFAULT '',
                "AiSummary" TEXT NOT NULL DEFAULT '',
                "NextAction" TEXT NOT NULL DEFAULT '',
                "Difficulty" INTEGER NOT NULL DEFAULT 2,
                "EstimatedMinutes" INTEGER NOT NULL DEFAULT 25,
                "Tags" TEXT NOT NULL DEFAULT '',
                "ConstellationName" TEXT NOT NULL DEFAULT '',
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
                "Strength" REAL NOT NULL DEFAULT 0.5,
                CONSTRAINT "FK_FocusTaskLinks_FocusTasks_SourceTaskId"
                    FOREIGN KEY ("SourceTaskId")
                    REFERENCES "FocusTasks" ("Id")
                    ON DELETE CASCADE,
                CONSTRAINT "FK_FocusTaskLinks_FocusTasks_TargetTaskId"
                    FOREIGN KEY ("TargetTaskId")
                    REFERENCES "FocusTasks" ("Id")
                    ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS "IX_FocusTaskLinks_SourceTaskId"
            ON "FocusTaskLinks" ("SourceTaskId");

            CREATE INDEX IF NOT EXISTS "IX_FocusTaskLinks_TargetTaskId"
            ON "FocusTaskLinks" ("TargetTaskId");

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_FocusTaskLinks_SourceTaskId_TargetTaskId"
            ON "FocusTaskLinks" ("SourceTaskId", "TargetTaskId");
            """;
        await command.ExecuteNonQueryAsync();

        await EnsureColumnAsync(connection, "FocusTasks", "OriginalInput", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "FocusTasks", "QuestTitle", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "FocusTasks", "QuestNarrative", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "FocusTasks", "CompletionNarrative", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "FocusTasks", "RewardName", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "FocusTasks", "AiSummary", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "FocusTasks", "NextAction", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "FocusTasks", "Difficulty", "INTEGER NOT NULL DEFAULT 2");
        await EnsureColumnAsync(connection, "FocusTasks", "EstimatedMinutes", "INTEGER NOT NULL DEFAULT 25");
        await EnsureColumnAsync(connection, "FocusTasks", "Tags", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "FocusTasks", "ConstellationName", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "FocusTasks", "Status", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "FocusTasks", "CompletedAt", "TEXT NULL");
        await EnsureColumnAsync(connection, "FocusTasks", "X", "REAL NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "FocusTasks", "Y", "REAL NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "FocusTasks", "NodeSize", "REAL NOT NULL DEFAULT 18");
        await EnsureColumnAsync(connection, "FocusTaskLinks", "Reason", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "FocusTaskLinks", "Strength", "REAL NOT NULL DEFAULT 0.5");
    }

    private async Task EnsureContextMemorySchemaAsync()
    {
        await using var connection = new SqliteConnection(BuildConnectionString(GetDatabasePath()));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS "ContextMemories" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ContextMemories" PRIMARY KEY AUTOINCREMENT,
                "Type" INTEGER NOT NULL DEFAULT 5,
                "Title" TEXT NOT NULL,
                "Content" TEXT NOT NULL,
                "Source" TEXT NOT NULL DEFAULT '',
                "Tags" TEXT NOT NULL DEFAULT '',
                "Weight" REAL NOT NULL DEFAULT 0.6,
                "ConstellationName" TEXT NOT NULL DEFAULT '',
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "LastUsedAt" TEXT NULL,
                "X" REAL NOT NULL DEFAULT 0,
                "Y" REAL NOT NULL DEFAULT 0,
                "NodeSize" REAL NOT NULL DEFAULT 18
            );

            CREATE INDEX IF NOT EXISTS "IX_ContextMemories_Type"
            ON "ContextMemories" ("Type");

            CREATE INDEX IF NOT EXISTS "IX_ContextMemories_UpdatedAt"
            ON "ContextMemories" ("UpdatedAt");
            """;
        await command.ExecuteNonQueryAsync();

        await EnsureColumnAsync(connection, "ContextMemories", "ConstellationName", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "ContextMemories", "X", "REAL NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "ContextMemories", "Y", "REAL NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "ContextMemories", "NodeSize", "REAL NOT NULL DEFAULT 18");
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        var exists = false;
        await using (var existsCommand = connection.CreateCommand())
        {
            existsCommand.CommandText = $"PRAGMA table_info(\"{tableName}\");";
            await using var reader = await existsCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {columnDefinition};";
        await alterCommand.ExecuteNonQueryAsync();
    }

    private async Task RepairFocusTaskGalaxyLayoutAsync()
    {
        await using var db = new PerelegansDbContext();
        var tasks = await db.FocusTasks
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();

        var changed = false;
        for (var index = 0; index < tasks.Count; index++)
        {
            var task = tasks[index];
            if (task.X <= 0 || task.Y <= 0 || task.NodeSize <= 0)
            {
                var (x, y) = CreateGalaxyCoordinate(index, task.Title);
                task.X = x;
                task.Y = y;
                task.NodeSize = CreateNodeSize(task.Title, task.Status == FocusTaskStatus.Completed);
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(task.AiSummary))
            {
                task.AiSummary = string.IsNullOrWhiteSpace(task.QuestNarrative)
                    ? $"聚焦完成：{task.Title}"
                    : task.QuestNarrative;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(task.NextAction))
            {
                task.NextAction = CreateFallbackNextAction(task.Title);
                changed = true;
            }

            if (task.Difficulty <= 0)
            {
                task.Difficulty = 2;
                changed = true;
            }

            if (task.EstimatedMinutes <= 0)
            {
                task.EstimatedMinutes = 25;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(task.Tags))
            {
                task.Tags = NormalizeTags(null, task.Title);
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(task.ConstellationName))
            {
                task.ConstellationName = CreateFallbackConstellationName(task.Title);
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync();
        }
    }

    private async Task RepairContextMemoryGalaxyLayoutAsync()
    {
        await using var db = new PerelegansDbContext();
        var memories = await db.ContextMemories
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        var changed = false;
        for (var index = 0; index < memories.Count; index++)
        {
            var memory = memories[index];
            if (memory.X <= 0 || memory.Y <= 0 || memory.NodeSize <= 0)
            {
                var (x, y) = CreateGalaxyCoordinate(index, memory.Title);
                memory.X = x;
                memory.Y = y;
                changed = true;
            }

            var nodeSize = CreateMemoryNodeSize(memory.Weight, memory.Content);
            if (Math.Abs(memory.NodeSize - nodeSize) > 0.01)
            {
                memory.NodeSize = nodeSize;
                changed = true;
            }

            var constellation = CreateMemoryConstellationName(memory.Type, memory.Tags, memory.Title);
            if (!string.Equals(memory.ConstellationName, constellation, StringComparison.Ordinal))
            {
                memory.ConstellationName = constellation;
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync();
        }
    }

    private async Task LinkRelatedTasksAsync(FocusTask newTask)
    {
        await using var db = new PerelegansDbContext();
        var newTerms = ExtractSemanticTerms(newTask);
        if (newTerms.Count == 0)
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
            var candidateTerms = ExtractSemanticTerms(candidate);
            var overlap = candidateTerms
                .Intersect(newTerms, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var sameConstellation = !string.IsNullOrWhiteSpace(newTask.ConstellationName) &&
                string.Equals(
                    newTask.ConstellationName,
                    candidate.ConstellationName,
                    StringComparison.OrdinalIgnoreCase);
            if (overlap.Count == 0 && !sameConstellation)
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
                Reason = sameConstellation
                    ? newTask.ConstellationName
                    : string.Join(", ", overlap.Take(3)),
                Strength = Math.Clamp((sameConstellation ? 0.45 : 0.3) + overlap.Count * 0.12, 0.35, 0.95)
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task RebuildSemanticTaskLinksAsync()
    {
        await using (var db = new PerelegansDbContext())
        {
            var taskCount = await db.FocusTasks.CountAsync();
            if (taskCount <= 1)
            {
                return;
            }

            await db.FocusTaskLinks.ExecuteDeleteAsync();
        }

        await using var orderedDb = new PerelegansDbContext();
        var tasks = await orderedDb.FocusTasks
            .AsNoTracking()
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();
        foreach (var task in tasks.Skip(1))
        {
            await LinkRelatedTasksAsync(task);
        }
    }

    private static (double X, double Y) CreateGalaxyCoordinate(int index, string title)
    {
        var angle = index * 2.399963229728653;
        var radius = Math.Min(290, 44 + Math.Sqrt(index + 1) * 38);
        var titleNudge = Math.Min(24, Math.Max(0, title.Length - 8) * 1.2);
        var x = 430 + Math.Cos(angle) * (radius + titleNudge);
        var y = 300 + Math.Sin(angle) * radius * 0.82;
        return (Math.Clamp(x, 40, 820), Math.Clamp(y, 36, 560));
    }

    private static double CreateNodeSize(string title, bool completed)
    {
        var baseSize = completed ? 28 : 18;
        return baseSize + Math.Min(12, Math.Max(0, title.Length - 4) / 6d);
    }

    private static double CreateMemoryNodeSize(double weight, string content)
    {
        var weightSize = Math.Clamp(weight, 0.1, 1.0) * 16;
        var contentSize = Math.Min(8, Math.Max(0, content.Length - 80) / 80d);
        return 14 + weightSize + contentSize;
    }

    private static string CreateMemoryConstellationName(ContextMemoryType type, string tags, string title)
    {
        var firstTag = tags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(tag => tag.Length >= 2);
        if (!string.IsNullOrWhiteSpace(firstTag))
        {
            return $"{firstTag}星座";
        }

        return type switch
        {
            ContextMemoryType.Preference => "偏好星座",
            ContextMemoryType.Project => "项目星座",
            ContextMemoryType.Decision => "决策星座",
            ContextMemoryType.Workflow => "工作流星座",
            ContextMemoryType.Application => "应用星座",
            _ => CreateFallbackConstellationName(title)
        };
    }

    private static string CreateMemoryTitle(string content)
    {
        var normalized = content.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return normalized.Length <= 24 ? normalized : normalized[..24];
    }

    private static HashSet<string> CreateSearchTerms(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var terms = ExtractKeywords(query);
        foreach (var chunk in query
                     .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(token => token.Length >= 2))
        {
            terms.Add(chunk.ToLowerInvariant());
        }

        return terms;
    }

    private static double ScoreMemory(ContextMemory memory, HashSet<string> terms)
    {
        var haystack = string.Join(' ', memory.Title, memory.Content, memory.Tags, memory.Source).ToLowerInvariant();
        var score = 0d;
        foreach (var term in terms)
        {
            if (haystack.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += term.Length >= 4 ? 2 : 1;
            }
        }

        if (score <= 0)
        {
            return 0;
        }

        var ageDays = Math.Max(0, (DateTime.Now - memory.UpdatedAt).TotalDays);
        var recencyBoost = Math.Max(0, 1.2 - ageDays / 30d);
        return score + memory.Weight * 2 + recencyBoost;
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

    private static HashSet<string> ExtractSemanticTerms(FocusTask task)
    {
        var text = string.Join(
            ' ',
            task.Title,
            task.AiSummary,
            task.NextAction,
            task.Tags,
            task.ConstellationName);
        return ExtractKeywords(text);
    }

    private static string NormalizeInsightText(params string?[] candidates)
    {
        return candidates
            .Select(candidate => candidate?.Trim())
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))
            ?? string.Empty;
    }

    private static string NormalizeTags(IEnumerable<string>? tags, string title)
    {
        var normalized = (tags ?? [])
            .Select(tag => tag.Trim().TrimStart('#'))
            .Where(tag => tag.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        if (normalized.Count == 0)
        {
            normalized = ExtractKeywords(title)
                .Take(4)
                .ToList();
        }

        return string.Join(", ", normalized);
    }

    private static string CreateFallbackNextAction(string title)
    {
        return $"先推进「{title}」最小可验证的一步。";
    }

    private static string CreateFallbackConstellationName(string title)
    {
        var firstKeyword = ExtractKeywords(title).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstKeyword)
            ? "行动星座"
            : $"{firstKeyword}星座";
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
