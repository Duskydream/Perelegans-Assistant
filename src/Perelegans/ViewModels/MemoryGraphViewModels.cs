using System;
using System.Globalization;

namespace Perelegans.ViewModels;

public sealed class GalaxyLinkViewModel(double x1, double y1, double x2, double y2, double strength)
{
    public double X1 { get; } = x1;
    public double Y1 { get; } = y1;
    public double X2 { get; } = x2;
    public double Y2 { get; } = y2;
    public double Opacity { get; } = Math.Clamp(strength, 0.25, 0.85);
}

public sealed class FishboneBranchViewModel(string title, string items, string tags, int openPlanCount, int branchIndex)
{
    public string Title { get; } = title;
    public string Items { get; } = items;
    public string Tags { get; } = tags;
    public int OpenPlanCount { get; } = openPlanCount;
    public int BranchIndex { get; } = branchIndex;
    public string BranchNumber => (BranchIndex + 1).ToString(CultureInfo.InvariantCulture);
    public string MetaText => OpenPlanCount > 0 ? $"{OpenPlanCount} open plan" : "context";
}
