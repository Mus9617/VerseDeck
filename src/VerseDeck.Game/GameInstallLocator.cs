using System.Text.RegularExpressions;

namespace VerseDeck.Game;

public sealed record GameInstall(string ChannelFolder, string ActionMapsPath, string Source);

/// <summary>Finds the LIVE folder of Star Citizen without asking the player.</summary>
public sealed partial class GameInstallLocator
{
    private static readonly string[] StandardFolders =
    [
        @"Program Files\Roberts Space Industries\StarCitizen\LIVE",
        @"Roberts Space Industries\StarCitizen\LIVE",
        @"RSI\StarCitizen\LIVE"
    ];

    private readonly string _launcherLogFolder;
    private readonly IReadOnlyList<string> _driveRoots;

    public GameInstallLocator(string launcherLogFolder, IReadOnlyList<string> driveRoots)
    {
        _launcherLogFolder = launcherLogFolder;
        _driveRoots = driveRoots;
    }

    public static GameInstallLocator ForThisMachine()
    {
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rsilauncher", "logs");
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .ToList();
        return new GameInstallLocator(logs, drives);
    }

    public static bool IsChannelFolder(string? folder)
    {
        return !string.IsNullOrWhiteSpace(folder)
            && (Directory.Exists(Path.Combine(folder, "user", "client", "0")) || File.Exists(Path.Combine(folder, "StarCitizen_Launcher.exe")));
    }

    public GameInstall? Locate(string? configuredFolder)
    {
        // A folder the player typed is never silently replaced: if it is wrong, they need to see that.
        if (!string.IsNullOrWhiteSpace(configuredFolder))
        {
            return IsChannelFolder(configuredFolder) ? Install(configuredFolder.Trim(), "Ajuste") : null;
        }

        var fromLauncher = FromLauncherLog();
        if (IsChannelFolder(fromLauncher))
        {
            return Install(fromLauncher!, "Lanzador");
        }

        var standard = _driveRoots
            .SelectMany(root => StandardFolders.Select(folder => Path.Combine(root, folder)))
            .FirstOrDefault(IsChannelFolder);
        return standard is null ? null : Install(standard, "Ruta habitual");
    }

    private static GameInstall Install(string folder, string source)
    {
        return new GameInstall(folder, Path.Combine(folder, "user", "client", "0", "Profiles", "default", "actionmaps.xml"), source);
    }

    private string? FromLauncherLog()
    {
        try
        {
            var log = new DirectoryInfo(_launcherLogFolder)
                .EnumerateFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (log is null)
            {
                return null;
            }

            using var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? folder = null;
            while (reader.ReadLine() is { } line)
            {
                var match = LaunchLine().Match(line);
                if (match.Success)
                {
                    // The log is JSON-like, so backslashes arrive doubled.
                    folder = match.Groups[1].Value.Replace(@"\\", @"\");
                }
            }

            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Greedy on purpose: the folder itself may contain parentheses, as in "Program Files (x86)".
    [GeneratedRegex(@"Launching Star Citizen LIVE from \((.+)\)")]
    private static partial Regex LaunchLine();
}
