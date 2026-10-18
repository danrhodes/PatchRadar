using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PatchRadar.Core;
using PatchRadar.Export;
using PatchRadar.Infra;
using PatchRadar.Scan;
using PatchRadar.Settings;
using PatchRadar.State;

namespace PatchRadar.Views;

/// <summary>
/// Every update in the history window (or all time) with filters and a symptom search over the cached KB text.
/// Typing "wifi" or "0x80070" in the symptom box answers "which update introduced this?".
/// </summary>
public sealed class AllUpdatesWindow : Window
{
    private sealed record GridRow(UpdateRow Row, Brush Brush)
    {
        public string Severity => Row.Severity.ToString();
        public string Kb => Row.Update.Kb;
        public string Title => Row.Update.Title;
        public string Category => Row.Update.Category.ToString();
        public string Installed => Row.Update.InstalledOn.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        public string Result => Row.Update.State.ToString();
        public string Reviewed => Row.Review switch { ReviewStatus.Reviewed => "Yes", ReviewStatus.ChangedSinceReviewed => "Changed", _ => "" };
        public string Notes => Row.Result.Summary ?? "";
    }

    private readonly ScanService _scan;
    private readonly SettingsHost _settings;
    private readonly Action<UpdateRow> _openDetail;
    private readonly ILog _log;
    private IReadOnlyList<UpdateRow> _all = Array.Empty<UpdateRow>();
    private IReadOnlyList<UpdateRow> _shown = Array.Empty<UpdateRow>();

    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserAddRows = false, Margin = new Thickness(12, 0, 12, 0),
    };
    private readonly ComboBox _severity = new() { Width = 100 };
    private readonly ComboBox _category = new() { Width = 130 };
    private readonly ComboBox _review = new() { Width = 170 };
    private readonly DatePicker _from = new() { Width = 150 };
    private readonly DatePicker _to = new() { Width = 150 };
    private readonly TextBox _text = new() { Width = 160 };
    private readonly TextBox _symptom = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _allTime = new() { Content = "All time", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBlock _count = new() { Margin = new Thickness(12, 6, 12, 8), Foreground = Brushes.Gray };
    private bool _loading;
    private bool _ready;

    private const string AnySeverity = "Any risk";
    private const string AnyCategory = "Any category";
    private const string AnyReview = "Any review state";
    private const string NotReviewed = "Not reviewed";
    private const string Reviewed = "Reviewed";
    private const string Changed = "Changed since reviewed";

    public AllUpdatesWindow(ScanService scan, SettingsHost settings, Action<UpdateRow> openDetail, ILog log)
    {
        _scan = scan; _settings = settings; _openDetail = openDetail; _log = log;
        Title = "PatchRadar - all updates";
        Width = 1100; Height = 640; MinWidth = 760; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ThemeMode = Application.Current?.ThemeMode ?? System.Windows.ThemeMode.None;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        _severity.ItemsSource = new[] { AnySeverity }.Concat(Enum.GetNames<Severity>()).ToList();
        _severity.SelectedIndex = 0;
        _category.ItemsSource = new[] { AnyCategory }.Concat(Enum.GetNames<UpdateCategory>()).ToList();
        _category.SelectedIndex = 0;
        _review.ItemsSource = new[] { AnyReview, NotReviewed, Reviewed, Changed };
        _review.SelectedIndex = 0;

        var filters = new WrapPanel { Margin = new Thickness(12, 12, 12, 4) };
        void Add(string label, UIElement el)
        {
            filters.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            if (el is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 12, 4);
            filters.Children.Add(el);
        }
        Add("Risk", _severity); Add("Category", _category); Add("Review", _review); Add("From", _from); Add("To", _to); Add("KB or title", _text);
        filters.Children.Add(_allTime);

        var symptomRow = new DockPanel { Margin = new Thickness(12, 0, 12, 8) };
        var label = new TextBlock { Text = "Symptom search", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(label, Dock.Left);
        symptomRow.Children.Add(label);
        _symptom.ToolTip = "Searches improvements, known issues and workarounds in the cached KB articles. Try: wifi, NTLM, 0x80070, BitLocker";
        symptomRow.Children.Add(_symptom);

        BuildColumns();

        var csv = Btn("CSV…", (_, _) => ExportActions.SaveAs(ExportFormat.Csv, _shown, this, _log));
        var json = Btn("JSON…", (_, _) => ExportActions.SaveAs(ExportFormat.Json, _shown, this, _log));
        var md = Btn("Markdown…", (_, _) => ExportActions.SaveAs(ExportFormat.Markdown, _shown, this, _log));
        var copy = Btn("Copy as Markdown", (_, _) => ExportActions.CopyMarkdown(_shown, _log));
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 12), HorizontalAlignment = HorizontalAlignment.Right };
        bar.Children.Add(new TextBlock { Text = "Export filtered list:", VerticalAlignment = VerticalAlignment.Center });
        foreach (var b in new[] { csv, json, md, copy }) bar.Children.Add(b);

        var top = new StackPanel();
        top.Children.Add(filters);
        top.Children.Add(symptomRow);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bar, Dock.Bottom);
        DockPanel.SetDock(_count, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bar);
        root.Children.Add(_count);
        root.Children.Add(_grid);
        Content = root;

        _severity.SelectionChanged += (_, _) => Refilter();
        _category.SelectionChanged += (_, _) => Refilter();
        _review.SelectionChanged += (_, _) => Refilter();
        _from.SelectedDateChanged += (_, _) => Refilter();
        _to.SelectedDateChanged += (_, _) => Refilter();
        _text.TextChanged += (_, _) => Refilter();
        _symptom.TextChanged += (_, _) => Refilter();
        _allTime.Click += async (_, _) => await LoadAsync();
        _grid.MouseDoubleClick += (_, e) =>
        {
            if (_grid.SelectedItem is GridRow g && e.OriginalSource is DependencyObject d && IsInRow(d)) _openDetail(g.Row);
        };
        _grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _grid.SelectedItem is GridRow g) { _openDetail(g.Row); e.Handled = true; }
        };

        Loaded += async (_, _) => await LoadAsync();
    }

    private static bool IsInRow(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is DataGridRow) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private static Button Btn(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        b.Click += click;
        return b;
    }

    private void BuildColumns()
    {
        var sev = new FrameworkElementFactory(typeof(StackPanel));
        sev.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var dot = new FrameworkElementFactory(typeof(TextBlock));
        dot.SetValue(TextBlock.TextProperty, "● ");
        dot.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(GridRow.Brush)));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(GridRow.Severity)));
        sev.AppendChild(dot);
        sev.AppendChild(name);
        _grid.Columns.Add(new DataGridTemplateColumn { Header = "Risk", Width = 80, CellTemplate = new DataTemplate { VisualTree = sev }, SortMemberPath = nameof(GridRow.Severity) });
        _grid.Columns.Add(Col("KB", nameof(GridRow.Kb), 90));
        _grid.Columns.Add(Col("Title", nameof(GridRow.Title), 380));
        _grid.Columns.Add(Col("Category", nameof(GridRow.Category), 100));
        _grid.Columns.Add(Col("Installed", nameof(GridRow.Installed), 130));
        _grid.Columns.Add(Col("Result", nameof(GridRow.Result), 80));
        _grid.Columns.Add(Col("Reviewed", nameof(GridRow.Reviewed), 80));
        _grid.Columns.Add(Col("Notes", nameof(GridRow.Notes), 300));
    }

    private static DataGridTextColumn Col(string header, string path, double width) =>
        new() { Header = header, Binding = new System.Windows.Data.Binding(path), Width = width };

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        _count.Text = "Loading…";
        try
        {
            var all = _allTime.IsChecked == true;
            _all = await Task.Run(() => _scan.ListCachedAsync(all, CancellationToken.None));
            _ready = true;
            Refilter();
        }
        catch (Exception ex)
        {
            _log.Error("All updates load failed", ex);
            _count.Text = "Could not load updates: " + ex.Message;
        }
        finally { _loading = false; }
    }

    private void Refilter()
    {
        if (!_ready) return;
        var c = new UpdateFilterCriteria
        {
            Severity = _severity.SelectedItem is string s && s != AnySeverity ? Enum.Parse<Severity>(s) : null,
            Category = _category.SelectedItem is string k && k != AnyCategory ? Enum.Parse<UpdateCategory>(k) : null,
            Review = _review.SelectedItem as string switch { NotReviewed => ReviewStatus.None, Reviewed => ReviewStatus.Reviewed, Changed => ReviewStatus.ChangedSinceReviewed, _ => null },
            From = _from.SelectedDate,
            To = _to.SelectedDate,
            Text = _text.Text,
            Symptom = _symptom.Text,
        };
        _shown = UpdateFilter.Apply(_all, c);
        var colours = _settings.Current.Appearance.Colors;
        _grid.ItemsSource = _shown.Select(r => new GridRow(r, SeverityBrushes.For(r.Severity, colours))).ToList();
        _count.Text = $"{_shown.Count} of {_all.Count} updates" + (_symptom.Text.Trim().Length > 0 ? " match the symptom search" : "");
    }
}
