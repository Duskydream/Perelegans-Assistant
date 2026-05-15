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
        await EnsureBreakpointSnapshotSchemaAsync();
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

    public async Task<ContextMemory?> GetLatestOpenPlanMemoryAsync()
    {
        await using var db = new PerelegansDbContext();
        return await db.ContextMemories
            .AsNoTracking()
            .Where(m => m.IsPlan && !m.IsCompleted && !m.IsAbandoned)
            .OrderByDescending(m => m.UpdatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<ContextMemory?> CompleteContextMemoryAsync(int id)
    {
        await using var db = new PerelegansDbContext();
        var memory = await db.ContextMemories.FirstOrDefaultAsync(m => m.Id == id);
        if (memory == null)
        {
            return null;
        }

        memory.IsCompleted = true;
        memory.CompletedAt = DateTime.Now;
        memory.IsAbandoned = false;
        memory.AbandonedAt = null;
        memory.Lifecycle = ContextMemoryLifecycle.Archived;
        memory.Weight = CreateEffectiveMemoryWeight(memory.Weight, memory.IsPlan, true, false);
        memory.AiWeightProfile = CreateMemoryWeightProfile(memory.IsPlan, true, false, memory.Weight);
        memory.UpdatedAt = DateTime.Now;
        memory.NodeSize = CreateMemoryNodeSize(memory.Weight, memory.Content);
        await db.SaveChangesAsync();
        return memory;
    }

    public async Task<ContextMemory?> AbandonContextMemoryAsync(int id)
    {
        await using var db = new PerelegansDbContext();
        var memory = await db.ContextMemories.FirstOrDefaultAsync(m => m.Id == id);
        if (memory == null)
        {
            return null;
        }

        memory.IsAbandoned = true;
        memory.AbandonedAt = DateTime.Now;
        memory.IsCompleted = false;
        memory.CompletedAt = null;
        memory.Lifecycle = ContextMemoryLifecycle.Archived;
        memory.Weight = CreateEffectiveMemoryWeight(memory.Weight, memory.IsPlan, false, true);
        memory.AiWeightProfile = CreateMemoryWeightProfile(memory.IsPlan, false, true, memory.Weight);
        memory.UpdatedAt = DateTime.Now;
        memory.NodeSize = CreateMemoryNodeSize(memory.Weight, memory.Content);
        await db.SaveChangesAsync();
        return memory;
    }

    public async Task<int> RefreshMemoryLifecycleForDailyReviewAsync()
    {
        await using var db = new PerelegansDbContext();
        var memories = await db.ContextMemories.ToListAsync();
        var changed = 0;
        foreach (var memory in memories)
        {
            var lifecycle = CreateDefaultLifecycle(memory.IsPlan, memory.IsCompleted, memory.IsAbandoned, memory.UpdatedAt);
            if (memory.Lifecycle == ContextMemoryLifecycle.Contradicted)
            {
                continue;
            }

            if (memory.Lifecycle != lifecycle)
            {
                memory.Lifecycle = lifecycle;
                changed++;
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync();
        }

        return changed;
    }

    public async Task<ContextMemory> UpsertContextMemoryAsync(
        string title,
        string content,
        ContextMemoryType type,
        string source,
        string tags,
        double weight,
        int? id = null,
        string memoryAxis = "",
        string aiDescription = "",
        string aiExplanation = "",
        string nextPrediction = "",
        bool? isPlan = null,
        bool? isCompleted = null,
        bool? isAbandoned = null,
        ContextMemoryLifecycle? lifecycle = null,
        bool suppressPlanDetection = false,
        string aiWeightProfile = "")
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) || IsGenericMemoryTitle(title)
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

        var normalizedTags = NormalizeMemoryTags(tags, normalizedContent, suppressPlanDetection);
        var planDetected = !suppressPlanDetection &&
            ((isPlan ?? false) || LooksLikePlanMemory(normalizedContent) || HasTag(normalizedTags, "plan"));
        if (planDetected)
        {
            normalizedTags = AddTag(normalizedTags, "plan");
            type = ContextMemoryType.Task;
        }

        memory.Title = normalizedTitle;
        memory.Content = normalizedContent;
        memory.Type = type;
        memory.Source = source.Trim();
        memory.Tags = normalizedTags;
        memory.IsPlan = planDetected;
        memory.MemoryAxis = NormalizeMemoryAxis(memoryAxis, type, planDetected);
        memory.AiDescription = NormalizeLongText(aiDescription, normalizedContent, 1200);
        memory.AiExplanation = NormalizeLongText(aiExplanation, CreateFallbackMemoryExplanation(type, normalizedTags, normalizedContent, planDetected), 2000);
        memory.NextPrediction = NormalizeLongText(nextPrediction, planDetected ? CreateFallbackNextAction(normalizedTitle) : string.Empty, 1200);
        memory.IsCompleted = isCompleted ?? (planDetected ? memory.IsCompleted : false);
        memory.IsAbandoned = isAbandoned ?? (planDetected ? memory.IsAbandoned : false);
        if (memory.IsCompleted)
        {
            memory.IsAbandoned = false;
        }

        if (memory.IsAbandoned)
        {
            memory.IsCompleted = false;
        }

        memory.CompletedAt = memory.IsCompleted
            ? memory.CompletedAt ?? DateTime.Now
            : null;
        memory.AbandonedAt = memory.IsAbandoned
            ? memory.AbandonedAt ?? DateTime.Now
            : null;
        memory.Lifecycle = lifecycle ?? CreateDefaultLifecycle(planDetected, memory.IsCompleted, memory.IsAbandoned, memory.UpdatedAt);
        memory.AiWeightProfile = string.IsNullOrWhiteSpace(aiWeightProfile)
            ? CreateMemoryWeightProfile(planDetected, memory.IsCompleted, memory.IsAbandoned, weight)
            : aiWeightProfile.Trim();
        memory.Weight = CreateEffectiveMemoryWeight(weight, planDetected, memory.IsCompleted, memory.IsAbandoned);
        memory.ConstellationName = CreateMemoryConstellationName(type, memory.Tags, normalizedTitle, normalizedContent);
        memory.NodeSize = CreateMemoryNodeSize(memory.Weight, normalizedContent);
        memory.UpdatedAt = DateTime.Now;

        await db.SaveChangesAsync();
        return memory;
    }

    public async Task<List<ApplicationUsageSession>> GetApplicationUsageSessionsSinceAsync(DateTime since)
    {
        await using var db = new PerelegansDbContext();
        return await db.ApplicationUsageSessions
            .AsNoTracking()
            .Where(s => s.StartTime >= since || s.EndTime >= since)
            .OrderByDescending(s => s.StartTime)
            .ToListAsync();
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

    public async Task<BreakpointSnapshot> SaveBreakpointSnapshotAsync(BreakpointSnapshot snapshot)
    {
        await using var db = new PerelegansDbContext();
        snapshot.CreatedAt = DateTime.Now;
        db.BreakpointSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        return snapshot;
    }

    public async Task MarkBreakpointSnapshotShownAsync(int id, DateTime returnedAt)
    {
        await using var db = new PerelegansDbContext();
        var snapshot = await db.BreakpointSnapshots.FirstOrDefaultAsync(item => item.Id == id);
        if (snapshot == null)
        {
            return;
        }

        snapshot.WasShown = true;
        snapshot.ReturnedAt = returnedAt;
        await db.SaveChangesAsync();
    }

    public async Task DismissBreakpointSnapshotAsync(int id)
    {
        await using var db = new PerelegansDbContext();
        var snapshot = await db.BreakpointSnapshots.FirstOrDefaultAsync(item => item.Id == id);
        if (snapshot == null)
        {
            return;
        }

        snapshot.IsDismissed = true;
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
            ConstellationName = CreateTaskConstellationName(
                normalizedTitle,
                NormalizeTags(adventure?.Tags, normalizedTitle),
                adventure?.Summary,
                adventure?.NextAction,
                adventure?.ConstellationName),
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
                "MemoryAxis" TEXT NOT NULL DEFAULT 'event',
                "AiDescription" TEXT NOT NULL DEFAULT '',
                "AiExplanation" TEXT NOT NULL DEFAULT '',
                "NextPrediction" TEXT NOT NULL DEFAULT '',
                "IsPlan" INTEGER NOT NULL DEFAULT 0,
                "IsCompleted" INTEGER NOT NULL DEFAULT 0,
                "CompletedAt" TEXT NULL,
                "IsAbandoned" INTEGER NOT NULL DEFAULT 0,
                "AbandonedAt" TEXT NULL,
                "Lifecycle" INTEGER NOT NULL DEFAULT 0,
                "AiWeightProfile" TEXT NOT NULL DEFAULT '',
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
        await EnsureColumnAsync(connection, "ContextMemories", "MemoryAxis", "TEXT NOT NULL DEFAULT 'event'");
        await EnsureColumnAsync(connection, "ContextMemories", "AiDescription", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "ContextMemories", "AiExplanation", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "ContextMemories", "NextPrediction", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "ContextMemories", "IsPlan", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "ContextMemories", "IsCompleted", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "ContextMemories", "CompletedAt", "TEXT NULL");
        await EnsureColumnAsync(connection, "ContextMemories", "IsAbandoned", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "ContextMemories", "AbandonedAt", "TEXT NULL");
        await EnsureColumnAsync(connection, "ContextMemories", "Lifecycle", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "ContextMemories", "AiWeightProfile", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "ContextMemories", "X", "REAL NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "ContextMemories", "Y", "REAL NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "ContextMemories", "NodeSize", "REAL NOT NULL DEFAULT 18");
    }

    private async Task EnsureBreakpointSnapshotSchemaAsync()
    {
        await using var connection = new SqliteConnection(BuildConnectionString(GetDatabasePath()));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS "BreakpointSnapshots" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_BreakpointSnapshots" PRIMARY KEY AUTOINCREMENT,
                "CreatedAt" TEXT NOT NULL,
                "LeftAt" TEXT NOT NULL,
                "ReturnedAt" TEXT NULL,
                "ProcessName" TEXT NOT NULL DEFAULT '',
                "WindowTitle" TEXT NOT NULL DEFAULT '',
                "ExecutablePath" TEXT NOT NULL DEFAULT '',
                "RelatedPlanTitle" TEXT NOT NULL DEFAULT '',
                "RelatedMemoryId" INTEGER NULL,
                "Summary" TEXT NOT NULL DEFAULT '',
                "Evidence" TEXT NOT NULL DEFAULT '',
                "NextStep" TEXT NOT NULL DEFAULT '',
                "WasShown" INTEGER NOT NULL DEFAULT 0,
                "IsDismissed" INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS "IX_BreakpointSnapshots_CreatedAt"
            ON "BreakpointSnapshots" ("CreatedAt");

            CREATE INDEX IF NOT EXISTS "IX_BreakpointSnapshots_WasShown"
            ON "BreakpointSnapshots" ("WasShown");
            """;
        await command.ExecuteNonQueryAsync();

        await EnsureColumnAsync(connection, "BreakpointSnapshots", "ReturnedAt", "TEXT NULL");
        await EnsureColumnAsync(connection, "BreakpointSnapshots", "WindowTitle", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "BreakpointSnapshots", "RelatedPlanTitle", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "BreakpointSnapshots", "RelatedMemoryId", "INTEGER NULL");
        await EnsureColumnAsync(connection, "BreakpointSnapshots", "Summary", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "BreakpointSnapshots", "Evidence", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "BreakpointSnapshots", "NextStep", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "BreakpointSnapshots", "WasShown", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "BreakpointSnapshots", "IsDismissed", "INTEGER NOT NULL DEFAULT 0");
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

            var taskConstellation = CreateTaskConstellationName(
                task.Title,
                task.Tags,
                task.AiSummary,
                task.NextAction,
                task.ConstellationName);
            if (!string.Equals(task.ConstellationName, taskConstellation, StringComparison.Ordinal))
            {
                task.ConstellationName = taskConstellation;
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
            if (IsGenericMemoryTitle(memory.Title) && !string.IsNullOrWhiteSpace(memory.Content))
            {
                memory.Title = CreateMemoryTitle(memory.Content);
                changed = true;
            }

            var suppressPlanDetection = memory.Type == ContextMemoryType.Review ||
                memory.Source.Contains("review", StringComparison.OrdinalIgnoreCase);
            var planDetected = !suppressPlanDetection &&
                (memory.IsPlan || LooksLikePlanMemory(memory.Content) || HasTag(memory.Tags, "plan"));
            if (planDetected && (!memory.IsPlan || memory.Type != ContextMemoryType.Task || !HasTag(memory.Tags, "plan")))
            {
                memory.IsPlan = true;
                memory.Type = ContextMemoryType.Task;
                memory.Tags = AddTag(memory.Tags, "plan");
                changed = true;
            }

            var normalizedTags = NormalizeMemoryTags(memory.Tags, memory.Content, suppressPlanDetection);
            if (!string.Equals(memory.Tags, normalizedTags, StringComparison.Ordinal))
            {
                memory.Tags = normalizedTags;
                changed = true;
            }

            var axis = NormalizeMemoryAxis(memory.MemoryAxis, memory.Type, memory.IsPlan);
            if (!string.Equals(memory.MemoryAxis, axis, StringComparison.Ordinal))
            {
                memory.MemoryAxis = axis;
                changed = true;
            }

            if (memory.IsCompleted && memory.CompletedAt == null)
            {
                memory.CompletedAt = memory.UpdatedAt;
                changed = true;
            }

            if (!memory.IsCompleted && memory.CompletedAt != null)
            {
                memory.CompletedAt = null;
                changed = true;
            }

            if (memory.IsAbandoned && memory.AbandonedAt == null)
            {
                memory.AbandonedAt = memory.UpdatedAt;
                changed = true;
            }

            if (!memory.IsAbandoned && memory.AbandonedAt != null)
            {
                memory.AbandonedAt = null;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(memory.AiDescription))
            {
                memory.AiDescription = NormalizeLongText(string.Empty, memory.Content, 1200);
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(memory.AiExplanation))
            {
                memory.AiExplanation = CreateFallbackMemoryExplanation(memory.Type, memory.Tags, memory.Content, memory.IsPlan);
                changed = true;
            }

            if (memory.IsPlan && string.IsNullOrWhiteSpace(memory.NextPrediction))
            {
                memory.NextPrediction = CreateFallbackNextAction(memory.Title);
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(memory.AiWeightProfile))
            {
                memory.AiWeightProfile = CreateMemoryWeightProfile(memory.IsPlan, memory.IsCompleted, memory.IsAbandoned, memory.Weight);
                changed = true;
            }

            var lifecycle = CreateDefaultLifecycle(memory.IsPlan, memory.IsCompleted, memory.IsAbandoned, memory.UpdatedAt);
            if (memory.Lifecycle != ContextMemoryLifecycle.Contradicted && memory.Lifecycle != lifecycle)
            {
                memory.Lifecycle = lifecycle;
                changed = true;
            }

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

            var constellation = CreateMemoryConstellationName(memory.Type, memory.Tags, memory.Title, memory.Content);
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

    private static double CreateEffectiveMemoryWeight(double requestedWeight, bool isPlan, bool isCompleted, bool isAbandoned)
    {
        var baseWeight = Math.Clamp(requestedWeight, 0.1, 1.0);
        if (!isPlan)
        {
            return baseWeight;
        }

        if (isAbandoned)
        {
            return Math.Clamp(baseWeight * 0.58, 0.22, 0.62);
        }

        return isCompleted
            ? Math.Clamp(baseWeight * 0.82, 0.35, 0.86)
            : Math.Clamp(baseWeight + 0.18, 0.72, 1.0);
    }

    private static string CreateMemoryWeightProfile(bool isPlan, bool isCompleted, bool isAbandoned, double requestedWeight)
    {
        var status = isPlan
            ? isAbandoned ? "abandoned" : isCompleted ? "completed" : "open"
            : "context";
        var planBoost = isPlan && !isCompleted && !isAbandoned ? 0.35 : isPlan && !isAbandoned ? 0.08 : 0.0;
        var completionPenalty = isPlan && isCompleted ? 0.18 : 0.0;
        var abandonmentPenalty = isPlan && isAbandoned ? 0.35 : 0.0;
        return
            $"{{\"base\":{Math.Clamp(requestedWeight, 0.1, 1.0):0.00},\"planBoost\":{planBoost:0.00},\"completionPenalty\":{completionPenalty:0.00},\"abandonmentPenalty\":{abandonmentPenalty:0.00},\"status\":\"{status}\"}}";
    }

    private static ContextMemoryLifecycle CreateDefaultLifecycle(bool isPlan, bool isCompleted, bool isAbandoned, DateTime updatedAt)
    {
        if (isCompleted || isAbandoned)
        {
            return ContextMemoryLifecycle.Archived;
        }

        if (!isPlan && updatedAt < DateTime.Now.AddDays(-45))
        {
            return ContextMemoryLifecycle.Stale;
        }

        return ContextMemoryLifecycle.Active;
    }

    private static string NormalizeMemoryAxis(string memoryAxis, ContextMemoryType type, bool isPlan)
    {
        if (isPlan || type == ContextMemoryType.Task)
        {
            return "task";
        }

        var normalized = memoryAxis.Trim().ToLowerInvariant();
        return normalized is "task" or "event" or "decision" or "workflow" or "preference" or "application" or "review"
            ? normalized
            : type switch
            {
                ContextMemoryType.Preference => "preference",
                ContextMemoryType.Decision => "decision",
                ContextMemoryType.Workflow => "workflow",
                ContextMemoryType.Application => "application",
                ContextMemoryType.Review => "review",
                _ => "event"
            };
    }

    private static string NormalizeLongText(string text, string fallback, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(text) ? fallback : text;
        normalized = normalized.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string CreateFallbackMemoryExplanation(
        ContextMemoryType type,
        string tags,
        string content,
        bool isPlan)
    {
        var axis = isPlan ? "任务/计划" : type switch
        {
            ContextMemoryType.Preference => "偏好",
            ContextMemoryType.Decision => "决策",
            ContextMemoryType.Workflow => "流程",
            ContextMemoryType.Application => "应用",
            ContextMemoryType.Project => "项目",
            _ => "事件"
        };
        var constellation = CreateMemoryConstellationName(type, tags, content, content);
        return $"按「{axis}」主轴保存；根据 tag「{tags}」与内容语义归入「{constellation}」。";
    }

    private static string CreateMemoryConstellationName(
        ContextMemoryType type,
        string tags,
        string title,
        string content)
    {
        var text = string.Join(' ', title, content, tags, type.ToString());
        return CreateTaxonomyConstellationName(text, type);
    }

    private static string CreateTaskConstellationName(
        string title,
        string tags,
        string? summary = null,
        string? nextAction = null,
        string? modelConstellation = null)
    {
        var text = string.Join(' ', title, tags, summary, nextAction, modelConstellation);
        return CreateTaxonomyConstellationName(text, null);
    }

    private static string CreateTaxonomyConstellationName(string text, ContextMemoryType? memoryType)
    {
        if (memoryType == ContextMemoryType.Review)
        {
            return "复盘";
        }

        var normalized = (text ?? string.Empty).ToLowerInvariant();
        var mentionsLearning = ContainsAny(normalized,
            "study", "learn", "course", "lesson", "exam", "review", "reading notes",
            "学习", "复习", "课程", "考试", "刷题", "读书", "阅读");
        var mentionsDevelopment = ContainsAny(normalized,
            "code", "coding", "dev", "develop", "development", "program", "programming", "repo", "project",
            "bug", "fix", "implement", "refactor", "test", "dotnet", "c#", "csharp",
            "代码", "开发", "编程", "项目", "仓库", "实现", "修复", "重构", "测试", "调试");

        if (mentionsLearning && mentionsDevelopment)
        {
            return "行动 / 学习与开发";
        }

        if (ContainsAny(normalized,
                "deep learning", "deeplearning", "machine learning", "neural", "cnn", "transformer", "ml",
                "深度学习", "机器学习", "神经网络", "模型训练", "算法学习"))
        {
            return "学习 / 深度学习";
        }

        if (ContainsAny(normalized,
                "study", "learn", "course", "lesson", "exam", "review", "reading notes",
                "学习", "复习", "课程", "考试", "刷题", "读书", "阅读"))
        {
            return "学习";
        }

        if (ContainsAny(normalized,
                "game", "gaming", "steam", "unity", "unreal", "play session",
                "游戏", "游玩", "娱乐"))
        {
            return "游戏";
        }

        if (ContainsAny(normalized,
                "wpf", "xaml", "avalonia", "winui", "desktop", "perelegans"))
        {
            return "开发 / 桌面应用";
        }

        if (ContainsAny(normalized,
                "frontend", "front-end", "react", "vue", "css", "html", "ui", "ux", "web"))
        {
            return "开发 / 前端";
        }

        if (ContainsAny(normalized,
                "backend", "api", "server", "database", "sql", "ef core", "http"))
        {
            return "开发 / 后端";
        }

        if (ContainsAny(normalized,
                "code", "coding", "dev", "develop", "development", "program", "programming", "repo", "project",
                "bug", "fix", "implement", "refactor", "test", "dotnet", "c#", "csharp",
                "代码", "开发", "编程", "项目", "仓库", "实现", "修复", "重构", "测试", "调试"))
        {
            return "开发";
        }

        if (ContainsAny(normalized,
                "write", "writing", "paper", "article", "draft", "document", "docx", "markdown",
                "写作", "论文", "文章", "文档", "草稿", "整理"))
        {
            return "写作";
        }

        if (ContainsAny(normalized,
                "design", "figma", "prototype", "layout", "visual",
                "设计", "原型", "视觉", "排版"))
        {
            return "设计";
        }

        if (ContainsAny(normalized,
                "data", "analysis", "spreadsheet", "excel", "chart", "report",
                "数据", "分析", "表格", "报表", "图表"))
        {
            return "数据";
        }

        if (ContainsAny(normalized,
                "meeting", "email", "chat", "message", "communicate",
                "会议", "沟通", "邮件", "消息", "讨论"))
        {
            return "沟通";
        }

        if (ContainsAny(normalized,
                "health", "home", "life", "habit", "finance",
                "生活", "健康", "习惯", "家庭", "财务"))
        {
            return "生活";
        }

        return memoryType switch
        {
            ContextMemoryType.Preference => "偏好",
            ContextMemoryType.Decision => "决策",
            ContextMemoryType.Workflow => "工作流",
            ContextMemoryType.Application => "应用",
            ContextMemoryType.Project => "项目",
            _ => "行动"
        };
    }

    private static string CreateMemoryTitle(string content)
    {
        var normalized = content.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return normalized.Length <= 24 ? normalized : normalized[..24];
    }

    private static bool IsGenericMemoryTitle(string title)
    {
        return title.Trim().ToLowerInvariant() is
            "笔记" or "记录" or "记忆" or "项目" or "任务" or "note" or "notes" or "memory" or "project" or "task";
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
        var haystack = string.Join(
            ' ',
            memory.Title,
            memory.Content,
            memory.Tags,
            memory.Source,
            memory.MemoryAxis,
            memory.AiDescription,
            memory.AiExplanation,
            memory.NextPrediction,
            memory.ConstellationName).ToLowerInvariant();
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
        var planBoost = memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned
            ? 1.8
            : memory.IsPlan && !memory.IsAbandoned ? 0.45 : 0;
        var lifecycleWeight = memory.Lifecycle switch
        {
            ContextMemoryLifecycle.Active => 1.0,
            ContextMemoryLifecycle.Stale => 0.72,
            ContextMemoryLifecycle.Archived => 0.62,
            ContextMemoryLifecycle.Contradicted => 0.35,
            _ => 1.0
        };
        return (score + memory.Weight * 2 + recencyBoost + planBoost) * lifecycleWeight;
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

    private static bool ContainsAny(string text, params string[] candidates)
    {
        return candidates.Any(candidate => text.Contains(candidate, StringComparison.OrdinalIgnoreCase));
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

    private static string NormalizeMemoryTags(string tags, string content, bool suppressPlanDetection = false)
    {
        var normalized = new List<string>();
        if (!suppressPlanDetection && (LooksLikePlanMemory(content) || HasTag(tags, "plan")))
        {
            normalized.Add("plan");
        }

        normalized.AddRange(CreateSemanticMemoryTags(content));
        normalized.AddRange(tags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeTag)
            .Where(IsUsefulMemoryTag));

        if (normalized.Count == 0)
        {
            normalized = ExtractKeywords(content)
                .Select(NormalizeTag)
                .Where(IsUsefulMemoryTag)
                .Take(6)
                .ToList();
        }

        if (normalized.Count == 0)
        {
            normalized.Add("memory");
        }

        return string.Join(", ", normalized.Distinct(StringComparer.OrdinalIgnoreCase).Take(8));
    }

    private static string AddTag(string tags, string tag)
    {
        var normalized = tags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeTag)
            .Where(IsUsefulMemoryTag)
            .ToList();
        if (!normalized.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            normalized.Insert(0, tag);
        }

        return string.Join(", ", normalized.Distinct(StringComparer.OrdinalIgnoreCase).Take(8));
    }

    private static bool HasTag(string tags, string tag)
    {
        return tags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(item => string.Equals(item.Trim().TrimStart('#'), tag, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikePlanMemory(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();
        return ContainsAny(normalized,
            "我要", "我计划", "我打算", "我准备", "我想要", "计划", "打算",
            "i will", "i plan", "i want to", "i'm going to", "going to", "plan to");
    }

    private static IEnumerable<string> CreateSemanticMemoryTags(string content)
    {
        var lower = content.ToLowerInvariant();
        if (ContainsAny(lower, "deep learning", "deeplearning", "深度学习"))
        {
            yield return "deep learning";
            yield return "dl";
            yield return "learn";
        }

        if (ContainsAny(lower, "machine learning", "机器学习"))
        {
            yield return "machine learning";
            yield return "ml";
            yield return "learn";
        }

        if (ContainsAny(lower, "学习", "复习", "课程", "阅读", "learn", "study", "course"))
        {
            yield return "learn";
        }

        if (ContainsAny(lower, "开发", "编程", "代码", "实现", "调试", "programming", "development", "coding", "code"))
        {
            yield return "development";
            yield return "code";
        }

        if (ContainsAny(lower, "wpf", "xaml", "avalonia", "winui"))
        {
            yield return "desktop";
            yield return "wpf";
        }
    }

    private static string NormalizeTag(string tag)
    {
        return tag.Trim().TrimStart('#').ToLowerInvariant();
    }

    private static bool IsUsefulMemoryTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 32)
        {
            return false;
        }

        if (ContainsAny(tag, "我要", "我计划", "我打算", "我准备", "我想要"))
        {
            return false;
        }

        return !ContainsCjk(tag);
    }

    private static bool ContainsCjk(string text)
    {
        return text.Any(character => character is >= '\u4e00' and <= '\u9fff');
    }

    private static string CreateFallbackNextAction(string title)
    {
        return $"先推进「{title}」最小可验证的一步。";
    }

    private static string CreateFallbackConstellationName(string title)
    {
        return CreateTaxonomyConstellationName(title, null);
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
