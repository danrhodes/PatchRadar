using System.Windows;
using Microsoft.Win32;
using AppTheme = PatchRadar.Settings.ThemeMode;
using WpfTheme = System.Windows.ThemeMode;

namespace PatchRadar.Views;

/// <summary>
/// System / Light / Dark using the built-in WPF Fluent theme, which styles every control
/// (menus, DataGrid, tabs, inputs) and the window chrome. Follows the OS live when set to System.
/// </summary>
public static class ThemeManager
{
    private static AppTheme _mode = AppTheme.System;

    public static void Initialise(AppTheme mode)
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _mode == AppTheme.System)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
        };
        Apply(mode);
    }

    public static void Apply(AppTheme mode)
    {
        _mode = mode;
        var app = Application.Current;
        if (app is null) return;
        app.ThemeMode = mode switch
        {
            AppTheme.Light => WpfTheme.Light,
            AppTheme.Dark => WpfTheme.Dark,
            _ => WpfTheme.System,
        };
        // Windows opened before the switch pick the theme up from the application.
        foreach (Window w in app.Windows) w.ThemeMode = app.ThemeMode;
    }

    /// <summary>True when the effective theme is dark (used for the few hand-drawn brushes).</summary>
    public static bool IsDark
    {
        get
        {
            if (_mode == AppTheme.Dark) return true;
            if (_mode == AppTheme.Light) return false;
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }
    }
}
