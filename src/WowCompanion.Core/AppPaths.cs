namespace WowCompanion.Core;

/// <summary>Where the app keeps its files. Game data is rebuildable; user.sqlite is the one worth backing up.</summary>
public sealed record AppPaths(string Root)
{
    public string DataDir => Path.Combine(Root, "data");
    public string CacheDir => Path.Combine(Root, "cache");
    public string UserDbPath => Path.Combine(Root, "user.sqlite");

    public static AppPaths Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WowCompanion"));

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(CacheDir);
    }
}
