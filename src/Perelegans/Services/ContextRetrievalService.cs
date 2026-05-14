using Perelegans.Models;

namespace Perelegans.Services;

public sealed class ContextRetrievalService
{
    private readonly DatabaseService _databaseService;

    public ContextRetrievalService(DatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    public async Task<IReadOnlyList<ContextMemory>> RetrieveAsync(
        string query,
        ForegroundFocusSnapshot? foregroundSnapshot = null,
        int limit = 8)
    {
        var enrichedQuery = foregroundSnapshot == null
            ? query
            : $"{query} {foregroundSnapshot.ProcessName} {foregroundSnapshot.ExecutablePath}";

        return await _databaseService.SearchContextMemoriesAsync(enrichedQuery, limit);
    }

    public static string BuildContextPack(IEnumerable<ContextMemory> memories)
    {
        var lines = memories
            .Select(memory =>
                $"- [{memory.Id}] {memory.TypeText}: {memory.Title}\n" +
                $"  Axis: {memory.MemoryAxis}; Constellation: {memory.ConstellationName}; Tags: {memory.Tags}\n" +
                $"  Plan: {memory.IsPlan}; Completed: {memory.IsCompleted}; Weight: {memory.Weight:0.00}; WeightProfile: {memory.AiWeightProfile}\n" +
                $"  Content: {memory.Content}\n" +
                $"  Description: {memory.AiDescription}\n" +
                $"  Explanation: {memory.AiExplanation}\n" +
                $"  NextPrediction: {memory.NextPrediction}")
            .ToList();

        return lines.Count == 0
            ? "No local memories matched this request."
            : string.Join("\n", lines);
    }
}
