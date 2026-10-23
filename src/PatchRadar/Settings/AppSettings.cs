using System.Text.Json;
using System.Text.Json.Serialization;
using PatchRadar.Core;

namespace PatchRadar.Settings;

public enum UpdateMode { Off, Notify, Auto }
public enum UpdateChannel { Stable, PreRelease }
public enum ThemeMode { System, Light, Dark }

/// <summary>Base class: unknown JSON keys survive a load/save round-trip.</summary>
public abstract class SettingsSection
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class GeneralSettings : SettingsSection
{
    public bool RunAtLogin { get; set; } = true;
    public bool LeftClickOpensMenu { get; set; } = true;
    public bool ToastOnNewRed { get; set; } = true;
    /// <summary>Draw the count of pending updates you have not looked at yet on the tray icon.</summary>
    public bool ShowIconBadge { get; set; } = true;
}

/// <summary>Nudges the user to restart after a long uptime. The switch, threshold and repeat interval are configurable.</summary>
public sealed class RebootReminderSettings : SettingsSection
{
    public bool Enabled { get; set; } = true;
    /// <summary>Remind once the PC has run this many days without a restart.</summary>
    public int AfterDays { get; set; } = 14;
    /// <summary>Remind again after this many days if still not restarted (0 = once per start-up only).</summary>
    public int RepeatDays { get; set; } = 3;
    /// <summary>Servers restart in planned maintenance windows and often have nobody at the tray, so they are off by default.</summary>
    public bool RemindOnServers { get; set; }
    /// <summary>Threshold used instead of AfterDays on Windows Server.</summary>
    public int ServerAfterDays { get; set; } = 45;

    public int AfterDaysFor(bool isServer) => isServer ? ServerAfterDays : AfterDays;
}

public sealed class AppUpdateSettings : SettingsSection
{
    public UpdateMode Mode { get; set; } = UpdateMode.Auto;
    public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;
    public int CheckIntervalHours { get; set; } = 24;
    public bool ApplyAutomatically { get; set; } = true;
    public string SkippedVersion { get; set; } = "";
}

public sealed class ScanningSettings : SettingsSection
{
    public bool ScanOnStartup { get; set; } = true;
    public int StartupDelayMinutes { get; set; } = 30;
    public int ScanIntervalMinutes { get; set; } = 360;
    public int CacheHoursRecent { get; set; } = 12;
    public int CacheDaysOlder { get; set; } = 7;
    public int RecentWindowDays { get; set; } = 30;
}

public sealed class UpdatesSettings : SettingsSection
{
    public int MenuItemCount { get; set; } = 15;
    public int HistoryDays { get; set; } = 90;
    public List<UpdateCategory> IncludeCategories { get; set; } = new()
    {
        UpdateCategory.Security, UpdateCategory.Cumulative, UpdateCategory.DotNet,
        UpdateCategory.ServicingStack, UpdateCategory.Driver, UpdateCategory.Office, UpdateCategory.Other,
    };
    public List<string> ExcludeTitleRegex { get; set; } = new() { "Security Intelligence Update", "Definition Update" };
    public bool ShowFailed { get; set; }
    /// <summary>Also list updates Windows has found but not installed yet, with their known issues.</summary>
    public bool ShowPending { get; set; } = true;
}

public sealed class SeveritySettings : SettingsSection
{
    public List<string> CriticalKeywords { get; set; } = new();
    public List<string> ChangePhrases { get; set; } = new();
    public bool TreatBehaviourChangesAsIssues { get; set; } = true;
    public bool DimReviewed { get; set; } = true;
}

public sealed class ColorSettings : SettingsSection
{
    public string Green { get; set; } = "#2E9E5B";
    public string Orange { get; set; } = "#E08A1E";
    public string Red { get; set; } = "#D63A3A";
    public string Grey { get; set; } = "#7A7F87";
}

public sealed class AppearanceSettings : SettingsSection
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    /// <summary>Text size for the tray menu and windows, in device-independent pixels (8-24). 0 = the Windows default.</summary>
    public int FontSize { get; set; }
    public ColorSettings Colors { get; set; } = new();
}

public sealed class NetworkSettings : SettingsSection
{
    public string Proxy { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;
    public string UserAgent { get; set; } = "";
    public int MaxConcurrent { get; set; } = 2;
}

public sealed class AdvancedSettings : SettingsSection
{
    public string LogLevel { get; set; } = "Information";
    public bool EnableReleaseHealthFallback { get; set; } = true;
    public bool EnableWebView2Fallback { get; set; }
    /// <summary>Offer an Uninstall button for removable updates. Administrators only; off by default.</summary>
    public bool EnableUninstall { get; set; }
}

public sealed class AppSettings : SettingsSection
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public GeneralSettings General { get; set; } = new();
    public RebootReminderSettings RebootReminder { get; set; } = new();
    public AppUpdateSettings AppUpdate { get; set; } = new();
    public ScanningSettings Scanning { get; set; } = new();
    public UpdatesSettings Updates { get; set; } = new();
    public SeveritySettings Severity { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public NetworkSettings Network { get; set; } = new();
    public AdvancedSettings Advanced { get; set; } = new();

    /// <summary>Clamp out-of-range values back to defaults. Returns a warning per fix.</summary>
    public List<string> Validate()
    {
        var warnings = new List<string>();
        void Fix(bool bad, string what, Action reset)
        {
            if (!bad) return;
            reset();
            warnings.Add($"Invalid {what}; reset to default");
        }

        Fix(RebootReminder.AfterDays is < 1 or > 365, "rebootReminder.afterDays", () => RebootReminder.AfterDays = 14);
        Fix(RebootReminder.ServerAfterDays is < 1 or > 365, "rebootReminder.serverAfterDays", () => RebootReminder.ServerAfterDays = 45);
        Fix(RebootReminder.RepeatDays is < 0 or > 365, "rebootReminder.repeatDays", () => RebootReminder.RepeatDays = 3);
        Fix(Appearance.FontSize != 0 && Appearance.FontSize is < 8 or > 24, "appearance.fontSize", () => Appearance.FontSize = 0);
        Fix(Updates.MenuItemCount is < 5 or > 100, "updates.menuItemCount", () => Updates.MenuItemCount = 15);
        Fix(Updates.HistoryDays < 0, "updates.historyDays", () => Updates.HistoryDays = 90);
        Fix(Scanning.StartupDelayMinutes < 0, "scanning.startupDelayMinutes", () => Scanning.StartupDelayMinutes = 30);
        Fix(Scanning.ScanIntervalMinutes < 0, "scanning.scanIntervalMinutes", () => Scanning.ScanIntervalMinutes = 360);
        Fix(Scanning.CacheHoursRecent < 1, "scanning.cacheHoursRecent", () => Scanning.CacheHoursRecent = 12);
        Fix(Scanning.CacheDaysOlder < 1, "scanning.cacheDaysOlder", () => Scanning.CacheDaysOlder = 7);
        Fix(Scanning.RecentWindowDays < 1, "scanning.recentWindowDays", () => Scanning.RecentWindowDays = 30);
        Fix(AppUpdate.CheckIntervalHours < 1, "appUpdate.checkIntervalHours", () => AppUpdate.CheckIntervalHours = 24);
        Fix(Network.TimeoutSeconds is < 1 or > 600, "network.timeoutSeconds", () => Network.TimeoutSeconds = 30);
        Fix(Network.MaxConcurrent is < 1 or > 8, "network.maxConcurrent", () => Network.MaxConcurrent = 2);
        foreach (var rx in Updates.ExcludeTitleRegex.ToList())
        {
            try { _ = new System.Text.RegularExpressions.Regex(rx); }
            catch (ArgumentException)
            {
                Updates.ExcludeTitleRegex.Remove(rx);
                warnings.Add($"Invalid regex '{rx}' removed from updates.excludeTitleRegex");
            }
        }
        return warnings;
    }

    public SeverityOptions ToSeverityOptions() =>
        SeverityOptions.Create(Severity.CriticalKeywords, Severity.ChangePhrases, Severity.TreatBehaviourChangesAsIssues);
}
