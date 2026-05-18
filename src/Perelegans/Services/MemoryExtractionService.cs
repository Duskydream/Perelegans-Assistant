using Perelegans.Models;

namespace Perelegans.Services;

public sealed class MemoryExtractionService
{
    private readonly DatabaseService _databaseService;
    private readonly FocusClassificationClient? _aiClient;

    public MemoryExtractionService(DatabaseService databaseService, FocusClassificationClient? aiClient)
    {
        _databaseService = databaseService;
        _aiClient = aiClient;
    }

    public async Task<ContextMemory?> SaveExplicitMemoryAsync(string userInput)
    {
        var content = NormalizeExplicitMemoryInput(userInput);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        return await _databaseService.UpsertContextMemoryAsync(
            CreateTitle(content),
            content,
            InferType(content),
            "manual",
            string.Join(", ", ExtractTags(content)),
            0.85,
            memoryAxis: LooksLikePlanMemory(content) ? "task" : "event",
            aiDescription: content,
            nextPrediction: LooksLikePlanMemory(content) ? CreatePlanNextPrediction(content) : string.Empty,
            isPlan: LooksLikePlanMemory(content));
    }

    public async Task<ContextMemory?> TryExtractAndSaveAsync(
        string userInput,
        IReadOnlyList<ContextMemory> retrievedMemories,
        bool allowAutoSave)
    {
        if (!allowAutoSave || _aiClient?.IsConfigured != true)
        {
            return null;
        }

        var candidate = await _aiClient.ExtractMemoryCandidateAsync(
            userInput,
            ContextRetrievalService.BuildContextPack(retrievedMemories));
        if (candidate is not { ShouldRemember: true } ||
            candidate.Confidence < 0.72 ||
            string.IsNullOrWhiteSpace(candidate.Content))
        {
            return null;
        }

        return await SaveCandidateForReviewAsync(candidate);
    }

    public async Task<ContextMemory?> SaveCandidateForReviewAsync(MemoryCandidateResult candidate)
    {
        if (candidate is not { ShouldRemember: true } ||
            string.IsNullOrWhiteSpace(candidate.Content))
        {
            return null;
        }

        var title = NormalizeCandidateTitle(candidate.Title, candidate.Content);
        return await _databaseService.UpsertContextMemoryAsync(
            title,
            candidate.Content,
            ParseType(candidate.Type),
            "ai-candidate-pending",
            string.Join(", ", candidate.Tags.Take(8)),
            Math.Clamp(candidate.Weight, 0.2, 1.0),
            memoryAxis: candidate.MemoryAxis,
            aiDescription: candidate.Description,
            aiExplanation: candidate.Explanation,
            nextPrediction: candidate.NextPrediction,
            isPlan: candidate.IsPlan || LooksLikePlanMemory(candidate.Content),
            isCompleted: candidate.IsCompleted,
            aiWeightProfile: candidate.WeightProfile,
            reviewStatus: ContextMemoryReviewStatus.Pending);
    }

    public static ContextMemoryType ParseType(string? type)
    {
        return type?.Trim().ToLowerInvariant() switch
        {
            "preference" or "偏好" => ContextMemoryType.Preference,
            "project" or "项目" => ContextMemoryType.Project,
            "decision" or "决策" => ContextMemoryType.Decision,
            "workflow" or "习惯" or "流程" => ContextMemoryType.Workflow,
            "application" or "app" or "应用" => ContextMemoryType.Application,
            "event" or "事件" => ContextMemoryType.Event,
            "task" or "plan" or "任务" or "计划" => ContextMemoryType.Task,
            _ => ContextMemoryType.Note
        };
    }

    private static string NormalizeExplicitMemoryInput(string userInput)
    {
        var text = userInput.Trim();
        var prefixes = new[]
        {
            "remember that",
            "remember:",
            "记住：",
            "记住:",
            "请记住",
            "帮我记住"
        };

        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return text[prefix.Length..].Trim();
            }
        }

        return text;
    }

    private static ContextMemoryType InferType(string content)
    {
        var lower = content.ToLowerInvariant();
        if (ContainsAny(lower, "prefer", "喜欢", "偏好", "不喜欢", "希望"))
        {
            return ContextMemoryType.Preference;
        }

        if (ContainsAny(lower, "project", "项目", "仓库", "repo"))
        {
            return ContextMemoryType.Project;
        }

        if (LooksLikePlanMemory(content))
        {
            return ContextMemoryType.Task;
        }

        if (ContainsAny(lower, "decide", "决定", "选择", "原因"))
        {
            return ContextMemoryType.Decision;
        }

        return ContextMemoryType.Note;
    }

    private static string CreateTitle(string content)
    {
        var normalized = content.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 24 ? normalized : normalized[..24];
    }

    public static string NormalizeCandidateTitle(string? title, string content)
    {
        var normalized = title?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized) || IsGenericTitle(normalized)
            ? CreateTitle(content)
            : normalized;
    }

    private static bool IsGenericTitle(string title)
    {
        return title.Trim().ToLowerInvariant() is
            "笔记" or "记录" or "记忆" or "项目" or "任务" or "note" or "notes" or "memory" or "project" or "task";
    }

    private static IEnumerable<string> ExtractTags(string content)
    {
        var tags = new List<string>();
        if (LooksLikePlanMemory(content))
        {
            tags.Add("plan");
        }

        tags.AddRange(CreateSemanticTags(content));
        tags.AddRange(content
            .Split([' ', '\r', '\n', '\t', ',', '.', ';', ':', '，', '。', '；', '：'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeTag)
            .Where(token => token.Length >= 2)
            .Where(IsUsefulTag));

        return tags
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8);
    }

    public static bool LooksLikePlanMemory(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();
        return ContainsAny(normalized,
            "我要", "我计划", "我打算", "我准备", "我想要", "计划", "打算",
            "i will", "i plan", "i want to", "i'm going to", "going to", "plan to");
    }

    private static string CreatePlanNextPrediction(string content)
    {
        var title = CreateTitle(content);
        return $"下一步先把「{title}」拆成一个可完成的小动作，并在完成后勾选。";
    }

    private static IEnumerable<string> CreateSemanticTags(string content)
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
    }

    private static string NormalizeTag(string tag)
    {
        return tag.Trim().TrimStart('#').ToLowerInvariant();
    }

    private static bool IsUsefulTag(string tag)
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

    private static bool ContainsAny(string text, params string[] candidates)
    {
        return candidates.Any(text.Contains);
    }
}
