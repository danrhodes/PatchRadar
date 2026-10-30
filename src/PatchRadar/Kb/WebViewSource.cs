using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PatchRadar.Core;
using PatchRadar.Infra;

namespace PatchRadar.Kb;

/// <summary>The DOM after rendering, plus the HTTP status of the final navigation (0 when unknown).</summary>
public sealed record RenderedPage(string Html, int HttpStatus);

/// <summary>Renders a page in a real browser engine and returns the resulting HTML. Behind an interface so tests need no browser.</summary>
public interface IPageRenderer
{
    /// <summary>False when the runtime is missing (common on Servers). The source then skips itself.</summary>
    bool IsAvailable { get; }

    /// <summary>The rendered page, or null when nothing usable appeared before the timeout.</summary>
    Task<RenderedPage?> RenderAsync(string url, CancellationToken ct);
}

/// <summary>
/// Source C: render the KB article in a hidden WebView2 when the plain request is blocked by a bot challenge.
/// Off by default (advanced.enableWebView2Fallback). Skips quietly when the runtime is not installed.
/// Remote content is parsed to text exactly like source A; it is never shown to the user.
/// </summary>
public sealed class WebViewSource : IKbSource
{
    private readonly IPageRenderer _renderer;
    private readonly RequestGate _gate;
    private readonly Func<bool> _enabled;
    private readonly ILog _log;
    private readonly Func<DateTime> _now;
    private bool _loggedMissing;

    public string Name => "support.microsoft.com (WebView2)";

    public WebViewSource(IPageRenderer renderer, RequestGate gate, Func<bool> enabled, ILog? log = null, Func<DateTime>? now = null)
    {
        _renderer = renderer;
        _gate = gate;
        _enabled = enabled;
        _log = log ?? NullLog.Instance;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public bool IsEnabled
    {
        get
        {
            if (!_enabled()) return false;
            if (_renderer.IsAvailable) return true;
            if (!_loggedMissing)
            {
                _loggedMissing = true;
                _log.Warn("WebView2 fallback is enabled but the WebView2 runtime is not installed; skipping it");
            }
            return false;
        }
    }

    public async Task<KbData> FetchAsync(string kb, CancellationToken ct)
    {
        var id = kb.ToUpperInvariant();
        var number = id.StartsWith("KB", StringComparison.Ordinal) ? id[2..] : id;
        var url = $"https://support.microsoft.com/help/{number}";
        try
        {
            var page = await _gate.RunAsync(() => _renderer.RenderAsync(url, ct), ct).ConfigureAwait(false);
            // A 404 means Microsoft has no article (the not-found page has no h1, so the renderer returns at once).
            if (page is { HttpStatus: 404 })
                return Fail(id, KbStatus.NotFound, "Microsoft has no KB article for this update (HTTP 404)");
            if (page is null || string.IsNullOrWhiteSpace(page.Html))
                return Fail(id, "WebView2 rendered no usable content (bot challenge not solved or page slow)");

            var data = KbParser.Parse(id, page.Html, Name, url, _now());
            if (data.Status != KbStatus.Ok) data.Source = Name;
            return data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"{kb}: WebView2 render failed: {ex.Message}");
            return Fail(id, "WebView2 render failed: " + ex.Message);
        }
    }

    private KbData Fail(string id, string reason) => Fail(id, KbStatus.Failed, reason);

    private KbData Fail(string id, KbStatus status, string reason) => new()
    {
        Kb = id, Source = Name, FetchedUtc = _now(), Status = status, LastError = reason,
    };
}

/// <summary>
/// Hidden WebView2 on the UI thread: a window parked far off-screen, never shown to the user.
/// Navigation is limited to Microsoft hosts, downloads and pop-ups are refused.
/// </summary>
public sealed class WebView2Renderer : IPageRenderer
{
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ContentTimeout = TimeSpan.FromSeconds(25);

    private readonly string _userDataFolder;
    private readonly ILog _log;
    private bool? _available;

    public WebView2Renderer(string userDataFolder, ILog? log = null)
    {
        _userDataFolder = userDataFolder;
        _log = log ?? NullLog.Instance;
    }

    public bool IsAvailable
    {
        get
        {
            if (_available is { } a) return a;
            try
            {
                _available = !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException or DllNotFoundException or BadImageFormatException)
            {
                _available = false;
            }
            return _available.Value;
        }
    }

    public async Task<RenderedPage?> RenderAsync(string url, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("No UI thread available for WebView2");
        return await dispatcher.InvokeAsync(() => RenderOnUiAsync(url, ct)).Task.Unwrap().ConfigureAwait(false);
    }

    private async Task<RenderedPage?> RenderOnUiAsync(string url, CancellationToken ct)
    {
        var win = new Window
        {
            Width = 1100, Height = 900, Left = -32000, Top = -32000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
        };
        var view = new WebView2
        {
            CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = _userDataFolder },
        };
        win.Content = view;
        try
        {
            Directory.CreateDirectory(_userDataFolder);
            win.Show();
            await view.EnsureCoreWebView2Async().ConfigureAwait(true);
            var core = view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.NavigationStarting += (_, e) =>
            {
                if (!IsAllowedNavigation(e.Uri)) { _log.Warn("WebView2 navigation blocked: " + e.Uri); e.Cancel = true; }
            };

            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var status = 0;
            core.NavigationCompleted += (_, e) => { status = e.HttpStatusCode; loaded.TrySetResult(); };
            core.Navigate(url);
            await WaitAsync(loaded.Task, NavigationTimeout, ct).ConfigureAwait(true);
            if (status == 404) return new RenderedPage("", 404);

            // A JavaScript challenge replaces the page after load: poll until a real article (an h1) is there.
            var deadline = DateTime.UtcNow + ContentTimeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var json = await view.ExecuteScriptAsync("document.querySelector('h1') ? document.documentElement.outerHTML : ''").ConfigureAwait(true);
                var html = JsonSerializer.Deserialize<string>(json);
                if (!string.IsNullOrEmpty(html)) return new RenderedPage(html, status);
                await Task.Delay(500, ct).ConfigureAwait(true);
            }
            return null;
        }
        finally
        {
            try { view.Dispose(); } catch { }
            try { win.Close(); } catch { }
        }
    }

    public static bool IsAllowedNavigation(string uri) =>
        uri.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase) ||
        (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
         (u.Host.Equals("microsoft.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase)));

    private static async Task WaitAsync(Task task, TimeSpan timeout, CancellationToken ct)
    {
        // Cancel the timeout timer as soon as the page has loaded instead of leaving it running.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var done = await Task.WhenAny(task, Task.Delay(timeout, stop.Token)).ConfigureAwait(true);
        stop.Cancel();
        ct.ThrowIfCancellationRequested();
        if (done != task) throw new TimeoutException("Page did not finish loading in " + timeout.TotalSeconds + " s");
    }
}
