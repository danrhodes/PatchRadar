namespace PatchRadar.Updates;

/// <summary>Plain-English meaning of the most common Windows Update failure codes.</summary>
public static class WuErrors
{
    private static readonly Dictionary<uint, string> Known = new()
    {
        [0x80070002] = "A file Windows needs was not found; the update cache or a servicing component is damaged. Resetting the Windows Update components or running DISM /RestoreHealth usually fixes it.",
        [0x80070005] = "Access denied. Security software or permissions blocked the install. Retry as an administrator and check endpoint protection.",
        [0x8007000D] = "The update data is invalid or corrupt. Clear the SoftwareDistribution download cache and retry.",
        [0x80070070] = "Not enough free disk space to install the update.",
        [0x80070643] = "Fatal error during installation. Often a damaged .NET or Defender component; check the CBS log for the real cause.",
        [0x80070BC9] = "A restart from an earlier install is still pending. Restart the PC and try again.",
        [0x80073712] = "The component store is corrupt or a file is missing. Run DISM /Online /Cleanup-Image /RestoreHealth, then sfc /scannow.",
        [0x800F081F] = "Source files for the update could not be found. Run DISM /RestoreHealth against a good source.",
        [0x800F0831] = "A previous update the package depends on is missing or damaged. Install the earlier update first, or repair the component store.",
        [0x800F0922] = "Servicing could not complete. Commonly low space on the System Reserved partition, a damaged .NET, or a VPN connection interfering. Disconnect VPNs and check free space.",
        [0x80240034] = "The update failed to download. Check the network, proxy and free disk space, then retry.",
        [0x8024402C] = "Windows Update could not reach its server. Check proxy and DNS settings, or the WSUS address.",
        [0x80244022] = "The update service answered 503 (busy). Wait and retry.",
    };

    public static string? Explain(int hresult) => Known.TryGetValue(unchecked((uint)hresult), out var s) ? s : null;

    /// <summary>"0x800F0922: Servicing could not complete. ..." or just the code when it is not in the table.</summary>
    public static string Describe(int hresult)
    {
        var code = $"0x{unchecked((uint)hresult):X8}";
        return Explain(hresult) is { } text ? $"{code}: {text}" : $"{code} (no plain-English note for this code yet)";
    }
}
