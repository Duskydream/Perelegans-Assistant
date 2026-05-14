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
            0.85);
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

        return await _databaseService.UpsertContextMemoryAsync(
            string.IsNullOrWhiteSpace(candidate.Title) ? CreateTitle(candidate.Content) : candidate.Title,
            candidate.Content,
            ParseType(candidate.Type),
            "ai-candidate",
            string.Join(", ", candidate.Tags.Take(8)),
            Math.Clamp(candidate.Weight, 0.2, 1.0));
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

    private static IEnumerable<string> ExtractTags(string content)
    {
        return content
            .Split([' ', '\r', '\n', '\t', ',', '.', ';', ':', '，', '。', '；', '：'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6);
    }

    private static bool ContainsAny(string text, params string[] candidates)
    {
        return candidates.Any(text.Contains);
    }
}
