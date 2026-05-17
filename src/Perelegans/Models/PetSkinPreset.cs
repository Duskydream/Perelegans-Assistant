namespace Perelegans.Models;

public sealed record PetSkinPreset(string Id, string DisplayName);

public static class PetSkinPresets
{
    public const string Pink = "pink";
    public const string WhiteOddEyes = "white-odd-eyes";
    public const string Black = "black";

    public static readonly IReadOnlyList<PetSkinPreset> All = new[]
    {
        new PetSkinPreset(Pink, "粉色小猫"),
        new PetSkinPreset(WhiteOddEyes, "异瞳白猫"),
        new PetSkinPreset(Black, "黑色小猫")
    };

    private static readonly Dictionary<string, string> KnownIds = All
        .ToDictionary(preset => preset.Id, preset => preset.Id, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Pink;
        }

        return KnownIds.TryGetValue(id.Trim(), out var normalized) ? normalized : Pink;
    }
}
