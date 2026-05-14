using Perelegans.Models;

namespace Perelegans.ViewModels;

public partial class MainViewModel
{
    private static IEnumerable<GalaxyLinkViewModel> CreateMemoryGalaxyLinks(IReadOnlyCollection<ContextMemory> memories)
    {
        var ordered = memories.OrderBy(memory => memory.CreatedAt).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                var source = ordered[i];
                var target = ordered[j];
                var strength = CalculateMemoryLinkStrength(source, target);
                if (strength <= 0)
                {
                    continue;
                }

                yield return new GalaxyLinkViewModel(
                    source.X + source.NodeSize / 2,
                    source.Y + source.NodeSize / 2,
                    target.X + target.NodeSize / 2,
                    target.Y + target.NodeSize / 2,
                    strength);
            }
        }
    }

    private static IEnumerable<FishboneBranchViewModel> CreateFishboneBranches(IReadOnlyCollection<ContextMemory> memories)
    {
        var groups = memories
            .GroupBy(memory => string.IsNullOrWhiteSpace(memory.ConstellationName) ? "未归类" : memory.ConstellationName)
            .OrderByDescending(group => group.Count(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned))
            .ThenByDescending(group => group.Count())
            .Take(8);

        var branchIndex = 0;
        foreach (var group in groups)
        {
            var items = group
                .OrderByDescending(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned)
                .ThenByDescending(memory => memory.Weight)
                .Take(6)
                .Select(memory =>
                {
                    var status = memory.IsPlan
                        ? memory.IsCompleted ? "done" : "open"
                        : memory.MemoryAxis;
                    return $"{memory.Title} [{status}]";
                })
                .ToList();
            var openPlans = group.Count(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned);
            yield return new FishboneBranchViewModel(
                group.Key,
                string.Join("  /  ", items),
                string.Join(", ", group.SelectMany(memory => SplitTagsForInsight(memory.Tags)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5)),
                openPlans,
                branchIndex++);
        }
    }

    private static double CalculateMemoryLinkStrength(ContextMemory source, ContextMemory target)
    {
        var sameConstellation = !string.IsNullOrWhiteSpace(source.ConstellationName) &&
            string.Equals(source.ConstellationName, target.ConstellationName, StringComparison.OrdinalIgnoreCase);
        var sourceTags = SplitTags(source.Tags);
        var targetTags = SplitTags(target.Tags);
        var overlap = sourceTags.Intersect(targetTags, StringComparer.OrdinalIgnoreCase).Count();
        var sourceTerms = ExtractMemoryLinkTerms(source);
        var targetTerms = ExtractMemoryLinkTerms(target);
        var termOverlap = sourceTerms.Intersect(targetTerms, StringComparer.OrdinalIgnoreCase).Count();

        if (!sameConstellation && overlap == 0 && termOverlap < 2)
        {
            return 0;
        }

        if (sameConstellation && overlap == 0 && termOverlap == 0 && IsBroadConstellation(source.ConstellationName))
        {
            return 0;
        }

        return Math.Clamp((sameConstellation ? 0.34 : 0) + overlap * 0.2 + termOverlap * 0.08, 0.22, 0.88);
    }

    private static HashSet<string> SplitTags(string tags)
    {
        return tags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => tag.Length >= 2)
            .Where(tag => !IsGenericLinkTerm(tag))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> ExtractMemoryLinkTerms(ContextMemory memory)
    {
        var text = string.Join(' ', memory.Title, memory.Content, memory.Tags, memory.ConstellationName);
        return text
            .Split([' ', '\r', '\n', '\t', ',', '.', ';', ':', '/', '\\', '|', '，', '。', '；', '：', '、', '（', '）', '(', ')'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.Trim().TrimStart('#').ToLowerInvariant())
            .Where(term => term.Length >= 2)
            .Where(term => !IsGenericLinkTerm(term))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsBroadConstellation(string constellation)
    {
        return constellation is "开发" or "学习" or "游戏" or "写作" or "设计" or "数据" or "沟通" or "生活" or "行动" or "行动 / 学习与开发" or "笔记";
    }

    private static bool IsGenericLinkTerm(string term)
    {
        return term.Trim().ToLowerInvariant() is
            "笔记" or "记录" or "记忆" or "项目" or "任务" or "工作" or "流程" or "应用" or "行动" or "星座" or
            "note" or "notes" or "memory" or "memories" or "project" or "task" or "workflow" or "work" or
            "app" or "application" or "action" or "focus" or
            "开发" or "学习" or "游戏" or "写作" or "设计" or "数据" or "沟通" or "生活" or "plan";
    }

    private static IEnumerable<GalaxyLinkViewModel> CreateGalaxyLinks(
        IReadOnlyCollection<FocusTask> tasks,
        IEnumerable<FocusTaskLink> links)
    {
        var byId = tasks.ToDictionary(t => t.Id);
        foreach (var link in links)
        {
            if (!byId.TryGetValue(link.SourceTaskId, out var source) ||
                !byId.TryGetValue(link.TargetTaskId, out var target))
            {
                continue;
            }

            yield return new GalaxyLinkViewModel(
                source.X + source.NodeSize / 2,
                source.Y + source.NodeSize / 2,
                target.X + target.NodeSize / 2,
                target.Y + target.NodeSize / 2,
                link.Strength);
        }
    }
}
