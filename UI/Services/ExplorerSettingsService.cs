using System.Text.Json;

namespace UI.Services;

public sealed class ExplorerSettings
{
    public string ItemFontFamily { get; set; } = "Segoe UI";
    public double ItemFontSize { get; set; } = 11;
    public string ItemFontColor { get; set; } = "#FFE8EDF4";
    public bool UseAcrylicBackground { get; set; } = true;
    public string BackgroundTintColor { get; set; } = "#FF191D23";
    public double BackgroundTintOpacity { get; set; } = 0.8;
    public double BackgroundLuminosityOpacity { get; set; } = 0.8;
    public string BackgroundFallbackColor { get; set; } = "#FF191D23";
    public List<string> Bookmarks { get; set; } = [];
    public List<string> RecentLocations { get; set; } = [];
    public List<SavedFolderSet> FolderSets { get; set; } = [];
    public ExplorerSessionState Session { get; set; } = new();
}

public sealed class ExplorerSessionState
{
    public List<string> LeftTabPaths { get; set; } = [];
    public List<string> RightTabPaths { get; set; } = [];
    public int LeftSelectedTab { get; set; }
    public int RightSelectedTab { get; set; }
    public List<ExplorerTabState> LeftTabs { get; set; } = [];
    public List<ExplorerTabState> RightTabs { get; set; } = [];
    public List<string> LeftBackHistory { get; set; } = [];
    public List<string> LeftForwardHistory { get; set; } = [];
    public List<string> RightBackHistory { get; set; } = [];
    public List<string> RightForwardHistory { get; set; } = [];
}

public sealed class ExplorerTabState
{
    public string Path { get; set; } = "";
    public string Filter { get; set; } = "";
    public int ViewIndex { get; set; } = 1;
    public double IconSize { get; set; } = 120;
    public string SortField { get; set; } = "Name";
    public bool SortDescending { get; set; }
    public List<string> SelectedPaths { get; set; } = [];
}

public sealed class SavedFolderSet
{
    public string Name { get; set; } = "";
    public string LeftPath { get; set; } = "";
    public string RightPath { get; set; } = "";
}

public enum SessionRecoveryMode
{
    None,
    TryRecentCheckpoint,
    UseLastCleanSession
}

public static class ExplorerSettingsService
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FirmwareExplorer");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
    private static readonly string SafeSessionPath = Path.Combine(SettingsDirectory, "last-clean-session.json");
    private static readonly string ActiveSessionPath = Path.Combine(SettingsDirectory, "session-active");

    public static ExplorerSettings Load(out string? error)
    {
        error = null;
        if (!File.Exists(SettingsPath))
            return new ExplorerSettings();

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<ExplorerSettings>(json)
                ?? throw new JsonException("The settings file is empty.");
            settings.Session ??= new ExplorerSessionState();
            settings.Bookmarks ??= [];
            settings.RecentLocations ??= [];
            settings.FolderSets ??= [];
            settings.Session.LeftTabPaths ??= [];
            settings.Session.RightTabPaths ??= [];
            settings.Session.LeftTabs ??= [];
            settings.Session.RightTabs ??= [];
            settings.Session.LeftBackHistory ??= [];
            settings.Session.LeftForwardHistory ??= [];
            settings.Session.RightBackHistory ??= [];
            settings.Session.RightForwardHistory ??= [];
            if (string.IsNullOrWhiteSpace(settings.ItemFontFamily))
            {
                settings.ItemFontFamily = "Segoe UI";
                error = "The saved item font family was invalid; using Segoe UI.";
            }
            if (!double.IsFinite(settings.ItemFontSize) || settings.ItemFontSize is < 6 or > 48)
            {
                settings.ItemFontSize = 11;
                error = "The saved item font size was invalid; using 11 pixels.";
            }
            if (!IsValidColor(settings.ItemFontColor))
            {
                settings.ItemFontColor = "#FFE8EDF4";
                error = "The saved item font color was invalid; using the default color.";
            }
            if (!IsValidColor(settings.BackgroundTintColor))
            {
                settings.BackgroundTintColor = "#FF191D23";
                error = "The saved background tint color was invalid; using the default color.";
            }
            if (!IsValidColor(settings.BackgroundFallbackColor))
            {
                settings.BackgroundFallbackColor = "#FF191D23";
                error = "The saved background fallback color was invalid; using the default color.";
            }
            if (!double.IsFinite(settings.BackgroundTintOpacity) || settings.BackgroundTintOpacity is < 0 or > 1)
            {
                settings.BackgroundTintOpacity = 0.8;
                error = "The saved background tint opacity was invalid; using the default.";
            }
            if (!double.IsFinite(settings.BackgroundLuminosityOpacity) ||
                settings.BackgroundLuminosityOpacity is < 0 or > 1)
            {
                settings.BackgroundLuminosityOpacity = 0.8;
                error = "The saved background luminosity opacity was invalid; using the default.";
            }
            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            error = $"Could not load settings from {SettingsPath}: {exception.Message}";
            return new ExplorerSettings();
        }
    }

    public static void Save(ExplorerSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        WriteAtomically(SettingsPath, json);
    }

    public static SessionRecoveryMode BeginSession()
    {
        Directory.CreateDirectory(SettingsDirectory);
        if (!File.Exists(ActiveSessionPath))
        {
            WriteAtomically(ActiveSessionPath, "active");
            return SessionRecoveryMode.None;
        }

        var previousState = File.ReadAllText(ActiveSessionPath);
        WriteAtomically(ActiveSessionPath, "recovery-attempted");
        return string.Equals(previousState, "recovery-attempted", StringComparison.Ordinal)
            ? SessionRecoveryMode.UseLastCleanSession
            : SessionRecoveryMode.TryRecentCheckpoint;
    }

    public static ExplorerSessionState? LoadLastCleanSession(out string? error)
    {
        error = null;
        if (!File.Exists(SafeSessionPath))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ExplorerSessionState>(File.ReadAllText(SafeSessionPath))
                ?? throw new JsonException("The saved clean session is empty.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            error = $"Could not load the last clean folder session: {exception.Message}";
            return null;
        }
    }

    public static void CompleteSession(ExplorerSettings settings)
    {
        Save(settings);
        var session = JsonSerializer.Serialize(settings.Session, new JsonSerializerOptions { WriteIndented = true });
        WriteAtomically(SafeSessionPath, session);
        File.Delete(ActiveSessionPath);
    }

    private static void WriteAtomically(string path, string contents)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                File.Replace(temporaryPath, path, null);
            else
                File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static bool IsValidColor(string? color) =>
        color is { Length: 9 } &&
        color[0] == '#' &&
        color.Skip(1).All(Uri.IsHexDigit);
}
