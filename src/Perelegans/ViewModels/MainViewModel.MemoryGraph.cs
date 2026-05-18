using Perelegans.Models;

namespace Perelegans.ViewModels;

public partial class MainViewModel
{
    private const int MaxMemoryGalaxyLinks = 420;
    private const int MaxMemoryGalaxyLinksPerNode = 5;
    private const int MaxMemoryConstellationNodes = 24;
    private const int MaxMemoryConstellationLinks = 80;
    private const int MaxExpandedRelatedMemoryNodes = 72;

    private static IEnumerable<MemoryConstellationNodeViewModel> CreateMemoryConstellationNodes(
        IReadOnlyCollection<ContextMemory> memories)
    {
        var groups = memories
            .GroupBy(memory => string.IsNullOrWhiteSpace(memory.ConstellationName) ? "未归类" : memory.ConstellationName.Trim())
            .OrderByDescending(group => group.Count(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned))
            .ThenByDescending(group => group.Count())
            .ThenByDescending(group => group.Average(memory => memory.Weight))
            .Take(MaxMemoryConstellationNodes)
            .ToList();

        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var angle = groups.Count <= 1
                ? -Math.PI / 2
                : -Math.PI / 2 + Math.PI * 2 * i / groups.Count;
            var radius = groups.Count <= 6 ? 210 : 250;
            var weight = group.Average(memory => memory.Weight);
            var openPlans = group.Count(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned);
            var nodeSize = Math.Clamp(54 + Math.Sqrt(group.Count()) * 12 + openPlans * 4, 62, 128);
            var x = 450 + Math.Cos(angle) * radius - nodeSize / 2;
            var y = 300 + Math.Sin(angle) * radius * 0.78 - nodeSize / 2;
            var top = group
                .OrderByDescending(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned)
                .ThenByDescending(memory => memory.Weight)
                .Take(3)
                .Select(memory => memory.Title);
            yield return new MemoryConstellationNodeViewModel(
                group.Key,
                string.Join(" / ", top),
                string.Join(", ", group.SelectMany(memory => SplitTagsForInsight(memory.Tags)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4)),
                group.Count(),
                openPlans,
                weight,
                Math.Clamp(x, 32, 800),
                Math.Clamp(y, 32, 540),
                nodeSize);
        }
    }

    private static IEnumerable<GalaxyLinkViewModel> CreateMemoryConstellationLinks(
        IReadOnlyCollection<ContextMemory> memories,
        IReadOnlyCollection<MemoryConstellationNodeViewModel> constellations)
    {
        if (constellations.Count < 2)
        {
            yield break;
        }

        var nodes = constellations.ToDictionary(node => node.Title, StringComparer.OrdinalIgnoreCase);
        var profiles = memories
            .Where(memory => !string.IsNullOrWhiteSpace(memory.ConstellationName) &&
                nodes.ContainsKey(memory.ConstellationName.Trim()))
            .Select(memory => new MemoryLinkProfile(
                memory,
                SplitTags(memory.Tags),
                ExtractMemoryLinkTerms(memory)))
            .ToList();
        var strengths = new Dictionary<(string Source, string Target), double>();
        for (var i = 0; i < profiles.Count; i++)
        {
            for (var j = i + 1; j < profiles.Count; j++)
            {
                var source = profiles[i];
                var target = profiles[j];
                var sourceConstellation = source.Memory.ConstellationName.Trim();
                var targetConstellation = target.Memory.ConstellationName.Trim();
                if (string.Equals(sourceConstellation, targetConstellation, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var strength = CalculateMemoryLinkStrength(source, target);
                if (strength <= 0)
                {
                    continue;
                }

                var key = string.Compare(sourceConstellation, targetConstellation, StringComparison.OrdinalIgnoreCase) <= 0
                    ? (sourceConstellation, targetConstellation)
                    : (targetConstellation, sourceConstellation);
                strengths[key] = Math.Max(strengths.GetValueOrDefault(key), strength);
            }
        }

        foreach (var link in strengths
                     .OrderByDescending(item => item.Value)
                     .Take(MaxMemoryConstellationLinks))
        {
            if (!nodes.TryGetValue(link.Key.Source, out var source) ||
                !nodes.TryGetValue(link.Key.Target, out var target))
            {
                continue;
            }

            yield return new GalaxyLinkViewModel(
                source.X + source.NodeSize / 2,
                source.Y + source.NodeSize / 2,
                target.X + target.NodeSize / 2,
                target.Y + target.NodeSize / 2,
                link.Value);
        }
    }

    private static List<ContextMemory> CreateExpandedConstellationMemorySlice(
        IReadOnlyCollection<ContextMemory> memories,
        string constellationName)
    {
        var primary = memories
            .Where(memory => string.Equals(
                memory.ConstellationName.Trim(),
                constellationName,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned)
            .ThenByDescending(memory => memory.Weight)
            .ThenByDescending(memory => memory.UpdatedAt)
            .ToList();
        if (primary.Count == 0)
        {
            return [];
        }

        var primaryProfiles = primary
            .Select(memory => new MemoryLinkProfile(
                memory,
                SplitTags(memory.Tags),
                ExtractMemoryLinkTerms(memory)))
            .ToList();
        var related = memories
            .Where(memory => !string.Equals(
                memory.ConstellationName.Trim(),
                constellationName,
                StringComparison.OrdinalIgnoreCase))
            .Select(memory =>
            {
                var profile = new MemoryLinkProfile(
                    memory,
                    SplitTags(memory.Tags),
                    ExtractMemoryLinkTerms(memory));
                var strength = primaryProfiles
                    .Select(primaryProfile => CalculateMemoryLinkStrength(primaryProfile, profile))
                    .DefaultIfEmpty(0)
                    .Max();
                return new { Memory = memory, Strength = strength };
            })
            .Where(item => item.Strength > 0)
            .OrderByDescending(item => item.Strength)
            .ThenByDescending(item => item.Memory.Weight)
            .ThenByDescending(item => item.Memory.UpdatedAt)
            .Take(MaxExpandedRelatedMemoryNodes)
            .Select(item => item.Memory);

        return primary.Concat(related).ToList();
    }

    private static IEnumerable<GalaxyLinkViewModel> CreateMemoryGalaxyLinks(IReadOnlyCollection<ContextMemory> memories)
    {
        var profiles = memories
            .OrderBy(memory => memory.CreatedAt)
            .Select(memory => new MemoryLinkProfile(
                memory,
                SplitTags(memory.Tags),
                ExtractMemoryLinkTerms(memory)))
            .ToList();
        var candidates = new List<MemoryLinkCandidate>();
        for (var i = 0; i < profiles.Count; i++)
        {
            for (var j = i + 1; j < profiles.Count; j++)
            {
                var source = profiles[i];
                var target = profiles[j];
                var strength = CalculateMemoryLinkStrength(source, target);
                if (strength <= 0)
                {
                    continue;
                }

                candidates.Add(new MemoryLinkCandidate(source.Memory, target.Memory, strength));
            }
        }

        var linkCountByMemory = new Dictionary<int, int>();
        var emittedLinks = 0;
        foreach (var candidate in candidates
                     .OrderByDescending(candidate => candidate.Strength)
                     .ThenBy(candidate => CalculateSquaredDistance(candidate.Source, candidate.Target)))
        {
            var sourceCount = linkCountByMemory.GetValueOrDefault(candidate.Source.Id);
            var targetCount = linkCountByMemory.GetValueOrDefault(candidate.Target.Id);
            if (sourceCount >= MaxMemoryGalaxyLinksPerNode ||
                targetCount >= MaxMemoryGalaxyLinksPerNode)
            {
                continue;
            }

            linkCountByMemory[candidate.Source.Id] = sourceCount + 1;
            linkCountByMemory[candidate.Target.Id] = targetCount + 1;
            yield return new GalaxyLinkViewModel(
                candidate.Source.X + candidate.Source.NodeSize / 2,
                candidate.Source.Y + candidate.Source.NodeSize / 2,
                candidate.Target.X + candidate.Target.NodeSize / 2,
                candidate.Target.Y + candidate.Target.NodeSize / 2,
                candidate.Strength);

            emittedLinks++;
            if (emittedLinks >= MaxMemoryGalaxyLinks)
            {
                yield break;
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
            var topMemories = group
                .OrderByDescending(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned)
                .ThenByDescending(memory => memory.Weight)
                .Take(6)
                .ToList();
            var items = topMemories
                .Select(memory =>
                {
                    var status = memory.IsPlan
                        ? memory.IsCompleted ? "done" : "open"
                        : memory.MemoryAxis;
                    return $"{memory.Title} [{status}]";
                })
                .ToList();
            var openPlans = group.Count(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned);
            var branchWeight = group.Any()
                ? group.Average(memory => memory.Weight)
                : 0.6;
            yield return new FishboneBranchViewModel(
                group.Key,
                string.Join("  /  ", items),
                string.Join(", ", group.SelectMany(memory => SplitTagsForInsight(memory.Tags)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5)),
                openPlans,
                branchIndex++,
                branchWeight,
                topMemories
                    .Select((memory, itemIndex) => new FishboneMemoryItemViewModel(memory, itemIndex))
                    .ToList());
        }
    }

    private static double CalculateMemoryLinkStrength(MemoryLinkProfile source, MemoryLinkProfile target)
    {
        var sameConstellation = !string.IsNullOrWhiteSpace(source.Memory.ConstellationName) &&
            string.Equals(source.Memory.ConstellationName, target.Memory.ConstellationName, StringComparison.OrdinalIgnoreCase);
        var overlap = source.Tags.Intersect(target.Tags, StringComparer.OrdinalIgnoreCase).Count();
        var termOverlap = source.Terms.Intersect(target.Terms, StringComparer.OrdinalIgnoreCase).Count();

        if (!sameConstellation && overlap == 0 && termOverlap < 2)
        {
            return 0;
        }

        if (sameConstellation && overlap == 0 && termOverlap == 0 && IsBroadConstellation(source.Memory.ConstellationName))
        {
            return 0;
        }

        return Math.Clamp((sameConstellation ? 0.34 : 0) + overlap * 0.2 + termOverlap * 0.08, 0.22, 0.88);
    }

    private static double CalculateSquaredDistance(ContextMemory source, ContextMemory target)
    {
        var dx = source.X - target.X;
        var dy = source.Y - target.Y;
        return dx * dx + dy * dy;
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

    private sealed record MemoryLinkProfile(
        ContextMemory Memory,
        HashSet<string> Tags,
        HashSet<string> Terms);

    private sealed record MemoryLinkCandidate(
        ContextMemory Source,
        ContextMemory Target,
        double Strength);
}
