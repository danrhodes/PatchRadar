using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using PatchRadar.Core;
using PatchRadar.Infra;

namespace PatchRadar.Updates;

/// <summary>Windows Update Agent history via late-bound COM (no interop assembly).</summary>
public sealed class WuaHistoryProvider : IUpdateHistoryProvider
{
    private readonly int _maxEntries;
    public WuaHistoryProvider(int maxEntries = 1000) => _maxEntries = maxEntries;

    public Task<IReadOnlyList<InstalledUpdate>> GetAsync(CancellationToken ct) =>
        Task.Run<IReadOnlyList<InstalledUpdate>>(() => Read(ct), ct);

    private IReadOnlyList<InstalledUpdate> Read(CancellationToken ct)
    {
        var list = new List<InstalledUpdate>();
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
        if (type is null) return list;

        object? session = null, searcher = null, history = null;
        try
        {
            session = Activator.CreateInstance(type)!;
            dynamic s = session;
            searcher = s.CreateUpdateSearcher();
            dynamic se = searcher;
            int total = se.GetTotalHistoryCount();
            if (total <= 0) return list;
            history = se.QueryHistory(0, Math.Min(total, _maxEntries));
            dynamic h = history;
            int count = h.Count;
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                object? entry = null;
                try
                {
                    entry = h[i];
                    dynamic e = entry;
                    string title = e.Title ?? "";
                    DateTime date = e.Date;
                    int result = e.ResultCode;
                    int op = e.Operation;
                    string? support = null;
                    var cats = new List<string>();
                    try
                    {
                        dynamic c = e.Categories;
                        for (var j = 0; j < (int)c.Count; j++) cats.Add((string)c[j].Name);
                    }
                    catch { /* categories are optional */ }
                    try { support = e.SupportUrl; } catch { }
                    var hresult = 0;
                    try { hresult = (int)e.HResult; } catch { /* optional */ }

                    var mapped = UpdateMerger.FromWua(new WuaEntry(
                        title, new DateTimeOffset(date.ToUniversalTime(), TimeSpan.Zero), result, op, cats, support, hresult));
                    if (mapped is not null) list.Add(mapped);
                }
                finally { Release(entry); }
            }
        }
        finally
        {
            Release(history); Release(searcher); Release(session);
        }
        return list;
    }

    private static void Release(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }
}

/// <summary>
/// Updates Windows Update knows about but has not installed. Searches Windows Update via late-bound COM: online asks Microsoft or WSUS, offline reads the
/// local store (what Windows last detected) with no network call.
/// </summary>
public sealed class WuaPendingProvider(ILog? log = null) : IPendingUpdateProvider
{
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";

    public Task<IReadOnlyList<InstalledUpdate>> GetAsync(bool online, CancellationToken ct) =>
        Task.Run<IReadOnlyList<InstalledUpdate>>(() => Read(online, ct), ct);

    /// <summary>One line naming the policy values that change what the Windows Update search can see (WSUS, deferrals, pinned release).</summary>
    private static string DescribePolicy()
    {
        try
        {
            var parts = new List<string>();
            foreach (var (path, names) in new[]
            {
                (PolicyKey, new[] { "WUServer", "DoNotConnectToWindowsUpdateInternetLocations", "SetPolicyDrivenUpdateSourceForQualityUpdates", "SetPolicyDrivenUpdateSourceForFeatureUpdates", "DeferQualityUpdates", "DeferQualityUpdatesPeriodInDays", "DeferFeatureUpdates", "ExcludeWUDriversInQualityUpdate", "TargetReleaseVersion", "ManagePreviewBuildsPolicyValue" }),
                (PolicyKey + @"\AU", new[] { "UseWUServer", "NoAutoUpdate" }),
            })
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                if (k is null) continue;
                foreach (var n in names) if (k.GetValue(n) is { } v) parts.Add($"{n}={v}");
            }
            return parts.Count == 0 ? "no Windows Update policy values set" : string.Join(", ", parts);
        }
        catch (Exception ex) { return "policy unreadable: " + ex.Message; }
    }

    private IReadOnlyList<InstalledUpdate> Read(bool online, CancellationToken ct)
    {
        var list = new List<InstalledUpdate>();
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
        if (type is null) { log?.Warn("Pending search: Microsoft.Update.Session is not registered; skipping"); return list; }

        object? session = null, searcher = null, result = null, updates = null;
        try
        {
            session = Activator.CreateInstance(type)!;
            dynamic s = session;
            searcher = s.CreateUpdateSearcher();
            dynamic se = searcher;
            se.Online = online;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            result = se.Search("IsInstalled=0 and IsHidden=0");
            dynamic r = result;
            updates = r.Updates;
            dynamic us = updates;
            int count = us.Count;
            string server = "?", resultCode = "?";
            try { server = $"{se.ServerSelection}/service {se.ServiceID}"; } catch { }
            try { resultCode = $"{r.ResultCode}"; } catch { }
            log?.Info($"Pending search (online={online}): WUA returned {count} update(s) in {sw.ElapsedMilliseconds} ms, result code {resultCode}, server {server}; {DescribePolicy()}");
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                object? entry = null;
                try
                {
                    entry = us[i];
                    dynamic e = entry;
                    string title = e.Title ?? "";
                    try { log?.Debug($"  WUA offer: {title} | browseOnly={e.BrowseOnly} downloaded={e.IsDownloaded} mandatory={e.IsMandatory} type={e.Type}"); } catch { }
                    var kbs = new List<string>();
                    try { dynamic ids = e.KBArticleIDs; for (var j = 0; j < (int)ids.Count; j++) kbs.Add((string)ids[j]); } catch { /* optional */ }
                    var cats = new List<string>();
                    try { dynamic c = e.Categories; for (var j = 0; j < (int)c.Count; j++) cats.Add((string)c[j].Name); } catch { /* optional */ }
                    string? support = null;
                    try { support = e.SupportUrl; } catch { }
                    var available = DateTime.UtcNow;
                    try { available = ((DateTime)e.LastDeploymentChangeTime).ToUniversalTime(); } catch { }

                    var mapped = UpdateMerger.FromPending(new WuaPendingEntry(
                        title, kbs, new DateTimeOffset(available, TimeSpan.Zero), cats, support));
                    if (mapped is not null) list.Add(mapped);
                }
                finally { Release(entry); }
            }
        }
        finally
        {
            Release(updates); Release(result); Release(searcher); Release(session);
        }
        return list;
    }

    private static void Release(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }
}

/// <summary>Win32_QuickFixEngineering: catches updates installed offline (MSU, DISM).</summary>
public sealed class QfeProvider : IUpdateHistoryProvider
{
    public Task<IReadOnlyList<InstalledUpdate>> GetAsync(CancellationToken ct) =>
        Task.Run<IReadOnlyList<InstalledUpdate>>(() =>
        {
            var list = new List<InstalledUpdate>();
            using var searcher = new ManagementObjectSearcher("SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering");
            foreach (ManagementObject mo in searcher.Get())
            {
                ct.ThrowIfCancellationRequested();
                using (mo)
                {
                    var id = mo["HotFixID"] as string;
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var kb = UpdateMerger.ExtractKb(id);
                    if (kb is null) continue;
                    var when = ParseDate(mo["InstalledOn"] as string) ?? DateTimeOffset.MinValue;
                    var desc = mo["Description"] as string ?? "Update";
                    var title = $"{desc} ({kb})";
                    list.Add(new InstalledUpdate(kb, title, when, UpdateState.Installed,
                        desc.Contains("Security", StringComparison.OrdinalIgnoreCase) ? UpdateCategory.Security : UpdateCategory.Other,
                        null, "QFE"));
                }
            }
            return list;
        }, ct);

    internal static DateTimeOffset? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        foreach (var culture in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture, new CultureInfo("en-US") })
            if (DateTime.TryParse(s, culture, DateTimeStyles.AssumeLocal, out var d))
                return new DateTimeOffset(d);
        return null;
    }
}

/// <summary>Runs both providers in parallel and merges. A failing provider degrades to the other.</summary>
public sealed class UpdateCollector : IUpdateHistoryProvider
{
    private readonly IReadOnlyList<IUpdateHistoryProvider> _providers;
    private readonly Infra.ILog _log;

    public UpdateCollector(IEnumerable<IUpdateHistoryProvider> providers, Infra.ILog? log = null)
    {
        _providers = providers.ToList();
        _log = log ?? Infra.NullLog.Instance;
    }

    public async Task<IReadOnlyList<InstalledUpdate>> GetAsync(CancellationToken ct)
    {
        var tasks = _providers.Select(async p =>
        {
            try { return await p.GetAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn($"{p.GetType().Name} failed: {ex.Message}");
                return (IReadOnlyList<InstalledUpdate>)Array.Empty<InstalledUpdate>();
            }
        }).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return UpdateMerger.Merge(results);
    }
}
