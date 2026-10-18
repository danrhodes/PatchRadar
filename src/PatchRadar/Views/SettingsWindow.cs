using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PatchRadar.Core;
using PatchRadar.Infra;
using PatchRadar.Settings;

namespace PatchRadar.Views;

/// <summary>
/// Edits every key of settings.json. Values are validated on Save; nothing is written until then.
/// Unknown keys are preserved because Save starts from a clone of the live settings.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly SettingsHost _host;
    private readonly ILog _log;
    private readonly TextBlock _status = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };

    // General
    private readonly CheckBox _runAtLogin = Check("Run at login");
    private readonly CheckBox _leftClick = Check("Left-click opens the menu (otherwise right-click only)");
    private readonly CheckBox _toast = Check("Show a notification when a new red update appears");

    // Scanning
    private readonly CheckBox _scanOnStartup = Check("Scan on startup");
    private readonly TextBox _startupDelay = new();
    private readonly TextBox _interval = new();
    private readonly TextBox _cacheHours = new();
    private readonly TextBox _cacheDays = new();
    private readonly TextBox _recentDays = new();

    // Updates
    private readonly TextBox _menuCount = new();
    private readonly TextBox _historyDays = new();
    private readonly Dictionary<UpdateCategory, CheckBox> _categories = new();
    private readonly TextBox _excludeRegex = Multi(70);
    private readonly CheckBox _showFailed = Check("Show failed installs");

    // Severity
    private readonly TextBox _keywords = Multi(160);
    private readonly TextBox _phrases = Multi(100);
    private readonly CheckBox _treatChanges = Check("Treat behaviour changes in Improvements as issues");
    private readonly CheckBox _dimReviewed = Check("Dim reviewed updates");

    // Appearance
    private readonly ComboBox _theme = new() { ItemsSource = Enum.GetValues<Settings.ThemeMode>() };
    private readonly Dictionary<Severity, TextBox> _colours = new();

    // App updates
    private readonly ComboBox _updMode = new() { ItemsSource = Enum.GetValues<UpdateMode>() };
    private readonly ComboBox _updChannel = new() { ItemsSource = Enum.GetValues<UpdateChannel>() };
    private readonly TextBox _updInterval = new();
    private readonly CheckBox _updAuto = Check("Apply updates automatically (otherwise stage and wait for \"Restart to update\")");
    private readonly TextBox _skipped = new();

    // Network
    private readonly TextBox _proxy = new();
    private readonly TextBox _timeout = new();
    private readonly TextBox _userAgent = new();
    private readonly TextBox _maxConcurrent = new();

    // Advanced
    private readonly ComboBox _logLevel = new() { ItemsSource = Enum.GetNames<Infra.LogLevel>() };
    private readonly CheckBox _releaseHealth = Check("Use Windows release health as a fallback and enrichment source");
    private readonly CheckBox _uninstall = Check("Allow uninstalling updates (administrators only)");
    private readonly CheckBox _webView2 = Check("Use a hidden WebView2 render when the support site blocks requests (needs the WebView2 runtime)");

    public SettingsWindow(SettingsHost host, ILog log)
    {
        _host = host;
        _log = log;
        Title = "PatchRadar settings";
        Width = 780; Height = 620; MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ThemeMode = Application.Current?.ThemeMode ?? System.Windows.ThemeMode.None;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };

        var tabs = new TabControl { Margin = new Thickness(12, 12, 12, 0), TabStripPlacement = Dock.Left };
        tabs.Items.Add(Tab("General", Stack(_runAtLogin, _leftClick, _toast)));
        tabs.Items.Add(Tab("Scanning", Stack(_scanOnStartup,
            Row("Startup delay (minutes, 0 = immediately)", _startupDelay),
            Row("Scan interval (minutes, 0 = never)", _interval),
            Row("Cache for recent updates (hours)", _cacheHours),
            Row("Cache for older updates (days)", _cacheDays),
            Row("Recent window (days)", _recentDays))));

        var catPanel = new WrapPanel();
        foreach (var c in Enum.GetValues<UpdateCategory>())
        {
            var cb = Check(c.ToString());
            cb.Margin = new Thickness(0, 0, 16, 4);
            _categories[c] = cb;
            catPanel.Children.Add(cb);
        }
        tabs.Items.Add(Tab("Updates", Stack(
            Row("Newest updates in the tray menu (5-100)", _menuCount),
            Row("History window (days, 0 = all)", _historyDays),
            Label("Categories to include"), catPanel,
            Label("Exclude titles matching these regular expressions (one per line)"), _excludeRegex,
            _showFailed)));

        var resetKw = MakeButton("Reset keywords to defaults", (_, _) => _keywords.Text = Join(DefaultKeywords.Critical));
        var resetPh = MakeButton("Reset phrases to defaults", (_, _) => _phrases.Text = Join(DefaultKeywords.ChangePhrases));
        tabs.Items.Add(Tab("Severity and Keywords", Stack(
            Label("Critical keywords (one per line; prefix re: for a regular expression). Red when matched in an active issue or behaviour change."), _keywords, resetKw,
            Label("Change phrases (one per line). An improvement containing one and a critical keyword is a behaviour change."), _phrases, resetPh,
            _treatChanges, _dimReviewed)));

        var colourRows = new List<UIElement>();
        foreach (var (sev, label) in new[] { (Severity.Green, "Green"), (Severity.Orange, "Orange"), (Severity.Red, "Red"), (Severity.Grey, "Grey") })
        {
            var tb = new TextBox();
            _colours[sev] = tb;
            colourRows.Add(Row($"{label} (hex, e.g. #2E9E5B)", tb));
        }
        tabs.Items.Add(Tab("Appearance", Stack(new UIElement[] { Row("Theme", _theme) }.Concat(colourRows).ToArray())));

        var clearSkip = MakeButton("Clear", (_, _) => _skipped.Text = "");
        tabs.Items.Add(Tab("App updates", Stack(
            Row("Mode", _updMode), Row("Channel", _updChannel),
            Row("Check every (hours)", _updInterval),
            _updAuto,
            Row("Skipped version", _skipped), clearSkip)));

        tabs.Items.Add(Tab("Network", Stack(
            Row("Proxy (blank = system proxy)", _proxy),
            Row("Timeout (seconds)", _timeout),
            Row("User-Agent (blank = browser default)", _userAgent),
            Row("Max concurrent requests (applies after restart)", _maxConcurrent))));

        tabs.Items.Add(Tab("Advanced", Stack(Row("Log level", _logLevel), _releaseHealth, _webView2,
            _uninstall,
            Label("Shows an Uninstall button on updates that Windows can really remove (the newest cumulative update, standalone updates). It is never shown to standard users, never for servicing stack updates, and every removal asks for confirmation and is recorded with your user name and the time. Off by default."))));

        var save = MakeButton("Save", Save_Click);
        save.IsDefault = true;
        var reset = MakeButton("Reset to defaults", (_, _) => { Load(new AppSettings()); _status.Text = "Defaults loaded. Press Save to keep them."; _status.Foreground = Brushes.Gray; });
        var open = MakeButton("Open settings file", (_, _) => OpenFile());
        var close = MakeButton("Close", (_, _) => Close());
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in new[] { open, reset, save, close }) buttons.Children.Add(b);

        var bottom = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(tabs);
        Content = root;

        Load(host.Current);
    }

    // ---- load / save --------------------------------------------------------------------

    private void Load(AppSettings s)
    {
        _runAtLogin.IsChecked = s.General.RunAtLogin;
        _leftClick.IsChecked = s.General.LeftClickOpensMenu;
        _toast.IsChecked = s.General.ToastOnNewRed;

        _scanOnStartup.IsChecked = s.Scanning.ScanOnStartup;
        _startupDelay.Text = s.Scanning.StartupDelayMinutes.ToString();
        _interval.Text = s.Scanning.ScanIntervalMinutes.ToString();
        _cacheHours.Text = s.Scanning.CacheHoursRecent.ToString();
        _cacheDays.Text = s.Scanning.CacheDaysOlder.ToString();
        _recentDays.Text = s.Scanning.RecentWindowDays.ToString();

        _menuCount.Text = s.Updates.MenuItemCount.ToString();
        _historyDays.Text = s.Updates.HistoryDays.ToString();
        foreach (var (c, cb) in _categories) cb.IsChecked = s.Updates.IncludeCategories.Contains(c);
        _excludeRegex.Text = Join(s.Updates.ExcludeTitleRegex);
        _showFailed.IsChecked = s.Updates.ShowFailed;

        // Empty list in the file means "built-in defaults": show the effective list.
        _keywords.Text = Join(s.Severity.CriticalKeywords.Count > 0 ? s.Severity.CriticalKeywords : DefaultKeywords.Critical);
        _phrases.Text = Join(s.Severity.ChangePhrases.Count > 0 ? s.Severity.ChangePhrases : DefaultKeywords.ChangePhrases);
        _treatChanges.IsChecked = s.Severity.TreatBehaviourChangesAsIssues;
        _dimReviewed.IsChecked = s.Severity.DimReviewed;

        _theme.SelectedItem = s.Appearance.Theme;
        _colours[Severity.Green].Text = s.Appearance.Colors.Green;
        _colours[Severity.Orange].Text = s.Appearance.Colors.Orange;
        _colours[Severity.Red].Text = s.Appearance.Colors.Red;
        _colours[Severity.Grey].Text = s.Appearance.Colors.Grey;

        _updMode.SelectedItem = s.AppUpdate.Mode;
        _updChannel.SelectedItem = s.AppUpdate.Channel;
        _updInterval.Text = s.AppUpdate.CheckIntervalHours.ToString();
        _updAuto.IsChecked = s.AppUpdate.ApplyAutomatically;
        _skipped.Text = s.AppUpdate.SkippedVersion;

        _proxy.Text = s.Network.Proxy;
        _timeout.Text = s.Network.TimeoutSeconds.ToString();
        _userAgent.Text = s.Network.UserAgent;
        _maxConcurrent.Text = s.Network.MaxConcurrent.ToString();

        _logLevel.SelectedItem = s.Advanced.LogLevel;
        if (_logLevel.SelectedItem is null) _logLevel.SelectedItem = nameof(Infra.LogLevel.Information);
        _releaseHealth.IsChecked = s.Advanced.EnableReleaseHealthFallback;
        _webView2.IsChecked = s.Advanced.EnableWebView2Fallback;
        _uninstall.IsChecked = s.Advanced.EnableUninstall;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        int Num(TextBox tb, string name, int min, int max)
        {
            if (int.TryParse(tb.Text.Trim(), out var v) && v >= min && v <= max) return v;
            errors.Add($"{name} must be a whole number from {min} to {max}.");
            return min;
        }
        string Colour(Severity sev, string name)
        {
            var t = _colours[sev].Text.Trim();
            try { _ = ColorConverter.ConvertFromString(t); return t; }
            catch (FormatException) { errors.Add($"{name} colour is not a valid colour (use #RRGGBB)."); return t; }
        }

        var s = SettingsHost.Clone(_host.Current);
        s.General.RunAtLogin = _runAtLogin.IsChecked == true;
        s.General.LeftClickOpensMenu = _leftClick.IsChecked == true;
        s.General.ToastOnNewRed = _toast.IsChecked == true;

        s.Scanning.ScanOnStartup = _scanOnStartup.IsChecked == true;
        s.Scanning.StartupDelayMinutes = Num(_startupDelay, "Startup delay", 0, 10080);
        s.Scanning.ScanIntervalMinutes = Num(_interval, "Scan interval", 0, 10080);
        s.Scanning.CacheHoursRecent = Num(_cacheHours, "Recent cache hours", 1, 720);
        s.Scanning.CacheDaysOlder = Num(_cacheDays, "Older cache days", 1, 365);
        s.Scanning.RecentWindowDays = Num(_recentDays, "Recent window", 1, 365);

        s.Updates.MenuItemCount = Num(_menuCount, "Menu items", 5, 100);
        s.Updates.HistoryDays = Num(_historyDays, "History window", 0, 3650);
        s.Updates.IncludeCategories = _categories.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
        if (s.Updates.IncludeCategories.Count == 0) errors.Add("Select at least one update category.");
        s.Updates.ExcludeTitleRegex = Lines(_excludeRegex.Text);
        foreach (var rx in s.Updates.ExcludeTitleRegex)
        {
            try { _ = new System.Text.RegularExpressions.Regex(rx); }
            catch (ArgumentException) { errors.Add($"Invalid regular expression: {rx}"); }
        }
        s.Updates.ShowFailed = _showFailed.IsChecked == true;

        var kws = Lines(_keywords.Text);
        var phr = Lines(_phrases.Text);
        foreach (var k in kws.Where(k => k.StartsWith("re:", StringComparison.OrdinalIgnoreCase)))
        {
            try { _ = new System.Text.RegularExpressions.Regex(k[3..]); }
            catch (ArgumentException) { errors.Add($"Invalid keyword regular expression: {k}"); }
        }
        // Store an empty list when the user kept the defaults, so future default changes still apply.
        s.Severity.CriticalKeywords = SameSet(kws, DefaultKeywords.Critical) ? new() : kws;
        s.Severity.ChangePhrases = SameSet(phr, DefaultKeywords.ChangePhrases) ? new() : phr;
        s.Severity.TreatBehaviourChangesAsIssues = _treatChanges.IsChecked == true;
        s.Severity.DimReviewed = _dimReviewed.IsChecked == true;

        s.Appearance.Theme = (Settings.ThemeMode)(_theme.SelectedItem ?? Settings.ThemeMode.System);
        s.Appearance.Colors.Green = Colour(Severity.Green, "Green");
        s.Appearance.Colors.Orange = Colour(Severity.Orange, "Orange");
        s.Appearance.Colors.Red = Colour(Severity.Red, "Red");
        s.Appearance.Colors.Grey = Colour(Severity.Grey, "Grey");

        s.AppUpdate.Mode = (UpdateMode)(_updMode.SelectedItem ?? UpdateMode.Auto);
        s.AppUpdate.Channel = (UpdateChannel)(_updChannel.SelectedItem ?? UpdateChannel.Stable);
        s.AppUpdate.CheckIntervalHours = Num(_updInterval, "Update check interval", 1, 720);
        s.AppUpdate.ApplyAutomatically = _updAuto.IsChecked == true;
        s.AppUpdate.SkippedVersion = _skipped.Text.Trim();

        s.Network.Proxy = _proxy.Text.Trim();
        if (s.Network.Proxy.Length > 0 && !Uri.TryCreate(s.Network.Proxy, UriKind.Absolute, out _))
            errors.Add("Proxy must be a full URL such as http://proxy:8080.");
        s.Network.TimeoutSeconds = Num(_timeout, "Timeout", 1, 600);
        s.Network.UserAgent = _userAgent.Text.Trim();
        s.Network.MaxConcurrent = Num(_maxConcurrent, "Max concurrent requests", 1, 8);

        s.Advanced.LogLevel = _logLevel.SelectedItem as string ?? "Information";
        s.Advanced.EnableReleaseHealthFallback = _releaseHealth.IsChecked == true;
        s.Advanced.EnableWebView2Fallback = _webView2.IsChecked == true;
        s.Advanced.EnableUninstall = _uninstall.IsChecked == true;

        if (errors.Count > 0)
        {
            _status.Foreground = Brushes.IndianRed;
            _status.Text = string.Join(" ", errors.Distinct());
            return;
        }

        try
        {
            _host.Apply(s);
            _status.Foreground = Brushes.SeaGreen;
            _status.Text = "Saved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Saving settings failed", ex);
            _status.Foreground = Brushes.IndianRed;
            _status.Text = "Could not write settings.json: " + ex.Message;
        }
    }

    private void OpenFile()
    {
        try
        {
            if (!File.Exists(_host.FilePath)) _host.Apply(SettingsHost.Clone(_host.Current));
            // .json often has no association on servers: notepad always works.
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_host.FilePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { _log.Error("Open settings file failed", ex); }
    }

    // ---- helpers ------------------------------------------------------------------------

    private static string Join(IEnumerable<string> items) => string.Join(Environment.NewLine, items);

    private static List<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) =>
        new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(b);

    private static CheckBox Check(string text) => new() { Content = text, Margin = new Thickness(0, 0, 0, 8) };

    private static TextBox Multi(double height) => new()
    {
        Height = height, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0, 0, 0, 8),
    };

    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4) };

    private static Button MakeButton(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        b.Click += click;
        return b;
    }

    private static FrameworkElement Row(string label, Control input)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        input.MinWidth = 140;
        input.HorizontalAlignment = HorizontalAlignment.Left;
        if (input is TextBox) input.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(input, 1);
        g.Children.Add(l);
        g.Children.Add(input);
        return g;
    }

    private static StackPanel Stack(params UIElement[] items)
    {
        var sp = new StackPanel { Margin = new Thickness(12) };
        foreach (var i in items) sp.Children.Add(i);
        return sp;
    }

    private static TabItem Tab(string header, UIElement content) => new()
    {
        Header = header,
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content },
    };
}
