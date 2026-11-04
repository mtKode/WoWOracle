namespace WowCompanion.Data;

/// <summary>
/// The client tables hold unused, test and placeholder items. This is a starting list; tune it after reading
/// the "removed" sample the Spike prints.
/// </summary>
public static class ItemFilters
{
    private static readonly string[] JunkMarkers =
        ["deprecated", "[dnt]", "(old)", "[ph]", "(ph)", "unused", "placeholder", "test item", "do not use"];

    public static bool IsJunkName(string name)
    {
        if (name.Length == 0) return true;
        if (name.StartsWith("zz", StringComparison.OrdinalIgnoreCase)) return true;
        return JunkMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
