using System.IO;
using System.Runtime.InteropServices;

namespace WowCompanion.Desktop;

/// <summary>
/// "WoW Oracle" in the Start menu (the user's own Programs folder, no admin needed), so Windows search and launchers
/// like PowerToys Run find the app wherever its zip was unzipped. The zip stays portable: nothing else is installed.
/// Only offered for the published exe ("WoW Oracle.exe"), never for a development build.
/// </summary>
public static class StartMenuShortcut
{
    public const string ExeName = "WoW Oracle.exe";

    public static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "WoW Oracle.lnk");

    /// <summary>The running exe, when it's the published one.</summary>
    public static string? PublishedExe =>
        Environment.ProcessPath is { } exe && string.Equals(Path.GetFileName(exe), ExeName, StringComparison.OrdinalIgnoreCase)
            ? exe
            : null;

    public static bool Exists => File.Exists(ShortcutPath);

    /// <summary>Creates (or repoints) the shortcut to this exe. False when Windows wouldn't let us.</summary>
    public static bool Create(string exe)
    {
        object? shell = null, link = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return false;
            shell = Activator.CreateInstance(type);
            dynamic s = shell!;
            link = s.CreateShortcut(ShortcutPath);
            dynamic l = link!;
            l.TargetPath = exe;
            l.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
            l.IconLocation = exe + ",0";
            l.Description = "WoW Oracle: a pocket companion for WoW Forever";
            l.Save();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>Where the shortcut points now, or null.</summary>
    public static string? Target()
    {
        if (!Exists) return null;
        object? shell = null, link = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            shell = Activator.CreateInstance(type);
            dynamic s = shell!;
            link = s.CreateShortcut(ShortcutPath);
            dynamic l = link!;
            return (string)l.TargetPath;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>
    /// The app was moved (or a newer version unzipped elsewhere): an existing shortcut is pointed at this exe.
    /// True when it was changed.
    /// </summary>
    public static bool FollowMove(string exe)
    {
        var target = Target();
        if (target is null || string.Equals(Path.GetFullPath(target), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) return false;
        return Create(exe);
    }
}
