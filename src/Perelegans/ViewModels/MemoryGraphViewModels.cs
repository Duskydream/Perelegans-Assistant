using System;
using System.Collections.Generic;
using System.Globalization;
using Perelegans.Models;

namespace Perelegans.ViewModels;

public sealed class GalaxyLinkViewModel(double x1, double y1, double x2, double y2, double strength)
{
    public double X1 { get; } = x1;
    public double Y1 { get; } = y1;
    public double X2 { get; } = x2;
    public double Y2 { get; } = y2;
    public double Opacity { get; } = Math.Clamp(strength, 0.25, 0.85);
}

public sealed class MemoryConstellationNodeViewModel(
    string title,
    string summary,
    string tags,
    int memoryCount,
    int openPlanCount,
    double weight,
    double x,
    double y,
    double nodeSize)
{
    public string Title { get; } = title;
    public string Summary { get; } = summary;
    public string Tags { get; } = tags;
    public int MemoryCount { get; } = memoryCount;
    public int OpenPlanCount { get; } = openPlanCount;
    public double Weight { get; } = weight;
    public double X { get; } = x;
    public double Y { get; } = y;
    public double NodeSize { get; } = nodeSize;
    public bool HasOpenPlans => OpenPlanCount > 0;
    public string CountText => OpenPlanCount > 0
        ? $"{MemoryCount} 条 · {OpenPlanCount} 个计划"
        : $"{MemoryCount} 条记忆";
    public double VisualOpacity => Weight < 0.35 ? 0.58 : 1.0;
}

public sealed class FishboneMemoryItemViewModel(ContextMemory memory, int itemIndex)
{
    public ContextMemory Memory { get; } = memory;
    public int Id => Memory.Id;
    public string Title => Memory.DisplayTitle;
    public string Preview => Memory.Preview;
    public string Tags => Memory.Tags;
    public string TypeText => Memory.TypeText;
    public string StatusText => Memory.PlanStatusText;
    public bool IsPlan => Memory.IsPlan;
    public bool IsCompleted => Memory.IsCompleted;
    public bool IsAbandoned => Memory.IsAbandoned;
    public double Weight => Memory.Weight;
    public int ItemIndex { get; } = itemIndex;
    public double VisualOpacity => Memory.VisualOpacity;
}

public sealed class FishboneBranchViewModel(
    string title,
    string items,
    string tags,
    int openPlanCount,
    int branchIndex,
    double weight,
    IReadOnlyList<FishboneMemoryItemViewModel> memories)
{
    public string Title { get; } = title;
    public string Items { get; } = items;
    public string Tags { get; } = tags;
    public IReadOnlyList<FishboneMemoryItemViewModel> Memories { get; } = memories;
    public int OpenPlanCount { get; } = openPlanCount;
    public int BranchIndex { get; } = branchIndex;
    public double Weight { get; } = weight;
    public bool IsUpper => BranchIndex % 2 == 0;
    public double BranchX => 80 + BranchIndex * 290;
    public double CardTop => IsUpper ? 14 : 318;
    public double RibAngle => IsUpper ? -50 : 50;
    public bool IsLowWeight => Weight < 0.35;
    public double VisualOpacity => IsLowWeight ? 0.5 : 1.0;
    public string BranchNumber => (BranchIndex + 1).ToString(CultureInfo.InvariantCulture);
    public string MetaText => OpenPlanCount > 0 ? $"{OpenPlanCount} open plan" : "context";
}
