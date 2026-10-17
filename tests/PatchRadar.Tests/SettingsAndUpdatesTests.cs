using System.Text.Json;
using PatchRadar.Core;
using PatchRadar.Settings;
using PatchRadar.Updates;

namespace PatchRadar.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pr-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class SettingsTests
{
    [Fact]
    public void FirstRun_CreatesFileWithDefaults()
    {
        using var t = new TempDir();
        var s = new SettingsStore(t.File("settings.json")).Load();
        Assert.True(File.Exists(t.File("settings.json")));
        Assert.Equal(15, s.Updates.MenuItemCount);
        Assert.Equal(UpdateMode.Auto, s.AppUpdate.Mode);
        Assert.Equal("#D63A3A", s.Appearance.Colors.Red);
    }

    [Fact]
    public void RoundTrip_PreservesValues()
    {
        using var t = new TempDir();
        var store = new SettingsStore(t.File("s.json"));
        var s = new AppSettings();
        s.Updates.MenuItemCount = 30;
        s.Appearance.Theme = ThemeMode.Dark;
        s.Severity.CriticalKeywords.Add("frob");
        store.Save(s);
        var back = store.Load();
        Assert.Equal(30, back.Updates.MenuItemCount);
        Assert.Equal(ThemeMode.Dark, back.Appearance.Theme);
        Assert.Equal(new[] { "frob" }, back.Severity.CriticalKeywords);
    }

    [Fact]
    public void CorruptFile_IsBackedUpAndReset()
    {
        using var t = new TempDir();
        var path = t.File("s.json");
        File.WriteAllText(path, "{ not json !!");
        var s = new SettingsStore(path).Load();
        Assert.Equal(15, s.Updates.MenuItemCount);
        Assert.True(File.Exists(path + ".bad"));
        Assert.Equal("{ not json !!", File.ReadAllText(path + ".bad"));
        JsonDocument.Parse(File.ReadAllText(path)); // valid again
    }

    [Fact]
    public void MissingKeys_AreFilledWithDefaults()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File("s.json"), """{ "updates": { "menuItemCount": 20 } }""");
        var s = new SettingsStore(t.File("s.json")).Load();
        Assert.Equal(20, s.Updates.MenuItemCount);
        Assert.Equal(90, s.Updates.HistoryDays);
        Assert.Equal(30, s.Scanning.StartupDelayMinutes);
    }

    [Fact]
    public void UnknownKeys_SurviveSave()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File("s.json"),
            """{ "futureTop": 1, "general": { "runAtLogin": false, "futureNested": "x" } }""");
        var store = new SettingsStore(t.File("s.json"));
        var s = store.Load();
        Assert.False(s.General.RunAtLogin);
        store.Save(s);
        var text = File.ReadAllText(t.File("s.json"));
        Assert.Contains("futureTop", text);
        Assert.Contains("futureNested", text);
    }

    [Fact]
    public void InvalidValues_FallBackToDefaults()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File("s.json"),
            """{ "updates": { "menuItemCount": 2 }, "appearance": { "theme": "Purple" }, "scanning": { "scanIntervalMinutes": "soon" } }""");
        var s = new SettingsStore(t.File("s.json")).Load();
        Assert.Equal(15, s.Updates.MenuItemCount);         // out of range 5-100
        Assert.Equal(ThemeMode.System, s.Appearance.Theme); // bad enum -> section default
        Assert.Equal(360, s.Scanning.ScanIntervalMinutes);  // bad type -> section default
    }
}

public class MergerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static InstalledUpdate U(string kb, int daysAgo, UpdateState st = UpdateState.Installed,
        UpdateCategory cat = UpdateCategory.Cumulative, string src = "WUA", string? title = null) =>
        new(kb, title ?? $"2026-10 Cumulative Update ({kb})", Now.AddDays(-daysAgo), st, cat, null, src);

    [Theory]
    [InlineData("2026-10 Cumulative Update for Windows 11 (KB5127648)", "KB5127648")]
    [InlineData("Security Update KB5001234 for something", "KB5001234")]
    [InlineData("No number here", null)]
    public void ExtractKb(string title, string? kb) => Assert.Equal(kb, UpdateMerger.ExtractKb(title));

    [Fact]
    public void Merge_KeepsNewestPerKb_AndPrefersWuaOnTie()
    {
        var wua = new[] { U("KB1", 5), U("KB2", 3) };
        var qfe = new[] { U("KB1", 5, src: "QFE"), U("KB3", 1, src: "QFE"), U("KB2", 10, src: "QFE") };
        var m = UpdateMerger.Merge(wua, qfe);
        Assert.Equal(new[] { "KB3", "KB2", "KB1" }, m.Select(x => x.Kb));
        Assert.Equal("WUA", m.Single(x => x.Kb == "KB1").Source);
        Assert.Equal(Now.AddDays(-3), m.Single(x => x.Kb == "KB2").InstalledOn);
    }

    [Fact]
    public void Merge_UninstallAfterInstall_Wins()
    {
        var m = UpdateMerger.Merge(new[] { U("KB1", 10), U("KB1", 2, UpdateState.Uninstalled) });
        Assert.Equal(UpdateState.Uninstalled, Assert.Single(m).State);
    }

    [Fact]
    public void Merge_ReinstallAfterUninstall_Wins()
    {
        var m = UpdateMerger.Merge(new[] { U("KB1", 10, UpdateState.Uninstalled), U("KB1", 2, src: "QFE") });
        Assert.Equal(UpdateState.Installed, Assert.Single(m).State);
    }

    [Theory]
    [InlineData(2, 1, UpdateState.Installed)]
    [InlineData(3, 1, UpdateState.Installed)]
    [InlineData(4, 1, UpdateState.Failed)]
    [InlineData(5, 1, UpdateState.Failed)]
    [InlineData(2, 2, UpdateState.Uninstalled)]
    [InlineData(4, 2, UpdateState.Installed)]
    public void FromWua_MapsResultAndOperation(int result, int op, UpdateState expected)
    {
        var u = UpdateMerger.FromWua(new WuaEntry("Update (KB1234567)", Now, result, op, Array.Empty<string>(), null));
        Assert.Equal(expected, u!.State);
    }

    [Fact]
    public void FromWua_NoKb_IsSkipped() =>
        Assert.Null(UpdateMerger.FromWua(new WuaEntry("Some driver", Now, 2, 1, Array.Empty<string>(), null)));

    [Fact]
    public void Filter_HistoryWindow_FailedAndUninstalled()
    {
        var s = new UpdatesSettings { HistoryDays = 90 };
        var all = new[]
        {
            U("KB1", 10), U("KB2", 200), U("KB3", 5, UpdateState.Failed), U("KB4", 5, UpdateState.Uninstalled),
        };
        Assert.Equal(new[] { "KB1" }, UpdateMerger.Filter(all, s, Now).Select(u => u.Kb));
        Assert.Equal(new[] { "KB1", "KB2" }, UpdateMerger.Filter(all, s, Now, allTime: true).Select(u => u.Kb));
        s.ShowFailed = true;
        Assert.Contains("KB3", UpdateMerger.Filter(all, s, Now).Select(u => u.Kb));
        s.HistoryDays = 0;
        Assert.Contains("KB2", UpdateMerger.Filter(all, s, Now).Select(u => u.Kb));
    }

    [Fact]
    public void Filter_CategoryAndTitleRegex()
    {
        var s = new UpdatesSettings();
        var all = new[]
        {
            U("KB1", 1),
            U("KB2", 1, cat: UpdateCategory.Definition, title: "Security Intelligence Update for Microsoft Defender (KB2267602)"),
            U("KB3", 1, title: "Definition Update for something (KB3)"),
        };
        Assert.Equal(new[] { "KB1" }, UpdateMerger.Filter(all, s, Now).Select(u => u.Kb));

        s.IncludeCategories.Remove(UpdateCategory.Cumulative);
        s.ExcludeTitleRegex.Clear();
        Assert.Empty(UpdateMerger.Filter(all, s, Now)); // Cumulative excluded, Definition not included
    }

    [Theory]
    [InlineData("2026-10 Cumulative Update for Windows 11 (KB5127648)", UpdateCategory.Cumulative)]
    [InlineData("Security Intelligence Update for Microsoft Defender Antivirus (KB2267602)", UpdateCategory.Definition)]
    [InlineData("2026-10 Cumulative Update for .NET Framework 3.5 (KB5044033)", UpdateCategory.DotNet)]
    [InlineData("Servicing Stack Update for Windows 11 (KB5046306)", UpdateCategory.ServicingStack)]
    [InlineData("Contoso - Net driver (KB9999999)", UpdateCategory.Driver)]
    public void Classify(string title, UpdateCategory expected) =>
        Assert.Equal(expected, UpdateMerger.Classify(title, null));
}
