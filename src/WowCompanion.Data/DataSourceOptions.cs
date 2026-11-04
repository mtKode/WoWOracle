namespace WowCompanion.Data;

/// <summary>
/// Everything that could change when the game ships is here, not in code. The URL shapes are what wago.tools
/// used when this was written; confirm them with the Spike's "builds" and "fetch" commands.
/// </summary>
public sealed record DataSourceOptions
{
    /// <summary>wago.tools product code. The beta shows as a Classic beta product; at release this may change.</summary>
    public string Product { get; init; } = "wowf";

    /// <summary>
    /// Classic Era product used to fill items WoW Forever doesn't ship rows for (e.g. Manual Crowd Pummeler).
    /// Null turns the fallback off.
    /// </summary>
    public string? FallbackProduct { get; init; } = "wow_classic_era";

    /// <summary>
    /// GitHub folder listing of the CMaNGOS Classic world database. Its *.sql.gz holds drops, vendors and quest
    /// rewards (server data the client doesn't have). Null turns item sources off.
    /// </summary>
    public string? SourcesListingUrl { get; init; } = "https://api.github.com/repos/cmangos/classic-db/contents/Full_DB";

    /// <summary>Tables taken from the fallback build.</summary>
    public IReadOnlyList<string> FallbackTables { get; init; } = ["ItemSparse", "ItemEffect", "ItemXItemEffect"];

    /// <summary>Optional filter, e.g. "1.60." to ignore other builds of the same product.</summary>
    public string? VersionPrefix { get; init; }

    public string BuildsUrl { get; init; } = "https://wago.tools/api/builds";

    /// <summary>{table} and {version} are replaced. Version is the full four-part string.</summary>
    public string CsvUrlTemplate { get; init; } = "https://wago.tools/db2/{table}/csv?build={version}";

    /// <summary>Tables the database build needs.</summary>
    public IReadOnlyList<string> Tables { get; init; } = ["Item", "ItemSparse"];
}
