namespace Perelegans.ViewModels;

public sealed class PetGrowthDimensionViewModel(
    string name,
    string description,
    double value,
    double target,
    string unit,
    string accentBrush)
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public double Value { get; } = Math.Max(0, value);
    public double Target { get; } = Math.Max(1, target);
    public string Unit { get; } = unit;
    public string AccentBrush { get; } = accentBrush;
    public double Percentage => Math.Clamp(Value / Target * 100d, 0d, 100d);
    public string ProgressText => $"{Math.Round(Value):0}/{Math.Round(Target):0} {Unit}";
}

public sealed class PetAbilityBadgeViewModel(
    string title,
    string description,
    string dimensionName,
    double requiredValue,
    double currentValue,
    string accentBrush)
{
    public string Title { get; } = title;
    public string Description { get; } = description;
    public string DimensionName { get; } = dimensionName;
    public double RequiredValue { get; } = requiredValue;
    public double CurrentValue { get; } = currentValue;
    public string AccentBrush { get; } = accentBrush;
    public bool IsUnlocked => CurrentValue >= RequiredValue;
    public double VisualOpacity => IsUnlocked ? 1.0 : 0.42;
    public string MarkerText => IsUnlocked ? "✓" : "·";
    public string StatusText => IsUnlocked
        ? "已解锁"
        : $"还差 {Math.Max(1, Math.Ceiling(RequiredValue - CurrentValue)):0}";
}

public sealed class PetRoomItemViewModel(
    string title,
    string description,
    string accentBrush)
{
    public string Title { get; } = title;
    public string Description { get; } = description;
    public string AccentBrush { get; } = accentBrush;
}
