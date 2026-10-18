using System.Text.Json;

namespace PatchRadar.Settings;

/// <summary>Holds the live settings object. Windows read <see cref="Current"/>; Save swaps it and raises <see cref="Changed"/>.</summary>
public sealed class SettingsHost
{
    private readonly SettingsStore _store;

    public SettingsHost(SettingsStore store, AppSettings initial)
    {
        _store = store;
        Current = initial;
    }

    public AppSettings Current { get; private set; }
    public string FilePath => _store.FilePath;
    public event Action<AppSettings>? Changed;

    /// <summary>Validate, persist and publish new settings. Returns validation warnings that were auto-fixed.</summary>
    public List<string> Apply(AppSettings updated)
    {
        var warnings = updated.Validate();
        _store.Save(updated);
        Current = updated;
        Changed?.Invoke(updated);
        return warnings;
    }

    /// <summary>Deep copy for editing in the Settings window (unknown keys survive).</summary>
    public static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}
