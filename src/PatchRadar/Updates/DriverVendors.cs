using System.Text.RegularExpressions;

namespace PatchRadar.Updates;

public sealed record DriverVendor(string Name, string SupportUrl);

/// <summary>
/// Drivers have no KB article, so for well-known hardware vendors the detail window offers the vendor's own support
/// or download page instead. The table is fixed and holds general landing pages, not deep links, which break often.
/// Only the URLs in this table can be opened as vendor links.
/// </summary>
public static class DriverVendors
{
    private sealed record Entry(DriverVendor Vendor, Regex Pattern);

    private static Entry E(string name, string url, params string[] keywords) =>
        new(new DriverVendor(name, url),
            new Regex(@"(?<![\w])(?:" + string.Join("|", keywords.Select(Regex.Escape)) + @")(?![\w])",
                      RegexOptions.IgnoreCase | RegexOptions.Compiled));

    private static readonly Entry[] Table =
    {
        // PC makers
        E("Lenovo", "https://support.lenovo.com", "Lenovo"),
        E("Dell", "https://www.dell.com/support/home", "Dell", "Alienware"),
        E("HP", "https://support.hp.com", "HP Inc", "HP", "Hewlett-Packard", "Hewlett Packard"),
        E("Hewlett Packard Enterprise", "https://support.hpe.com", "Hewlett Packard Enterprise", "HPE"),
        E("ASUS", "https://www.asus.com/support/", "ASUS", "ASUSTeK"),
        E("Acer", "https://www.acer.com/us-en/support", "Acer"),
        E("MSI", "https://www.msi.com/support", "MSI", "Micro-Star"),
        E("Gigabyte", "https://www.gigabyte.com/Support", "Gigabyte"),
        E("Samsung", "https://www.samsung.com/us/support/", "Samsung"),
        E("Dynabook / Toshiba", "https://support.dynabook.com", "Dynabook", "Toshiba"),
        E("Fujitsu", "https://www.fujitsu.com/global/support/", "Fujitsu"),
        E("Huawei", "https://consumer.huawei.com/en/support/", "Huawei"),
        E("Sony", "https://www.sony.com/electronics/support", "Sony"),
        E("Supermicro", "https://www.supermicro.com/en/support", "Supermicro"),
        E("Microsoft Surface", "https://support.microsoft.com/surface", "Surface"),

        // Chip and component makers
        E("Intel", "https://www.intel.com/content/www/us/en/download-center/home.html", "Intel"),
        E("AMD", "https://www.amd.com/en/support", "AMD", "Advanced Micro Devices", "ATI"),
        E("NVIDIA", "https://www.nvidia.com/en-us/drivers/", "NVIDIA"),
        E("Realtek", "https://www.realtek.com/Download", "Realtek"),
        E("Qualcomm", "https://www.qualcomm.com/support", "Qualcomm", "Atheros"),
        E("Broadcom", "https://www.broadcom.com/support/download-search", "Broadcom"),
        E("Goodix", "https://www.goodix.com", "Goodix"),
        E("ELAN Microelectronics", "https://www.emc.com.tw", "ELAN"),
        E("Synaptics", "https://www.synaptics.com/", "Synaptics"),
        E("MediaTek", "https://www.mediatek.com/", "MediaTek"),
        E("Dolby", "https://www.dolby.com/en-gb/about/support/", "Dolby"),
        E("Cirrus Logic", "https://www.cirrus.com/", "Cirrus Logic", "Cirrus"),
        E("Poly", "https://support.hp.com/gb-en/poly", "Poly", "Plantronics"),
        E("DisplayLink", "https://www.synaptics.com/products/displaylink-graphics/downloads", "DisplayLink"),

        // Peripherals
        E("Logitech", "https://support.logi.com", "Logitech"),
        E("Razer", "https://support.razer.com", "Razer"),
        E("Corsair", "https://help.corsair.com", "Corsair"),
        E("Creative", "https://support.creative.com", "Creative Technology", "Creative Labs"),
        E("Jabra", "https://www.jabra.com/support", "Jabra"),
        E("Wacom", "https://www.wacom.com/support", "Wacom"),
        E("Yubico", "https://support.yubico.com", "Yubico", "YubiKey"),

        // Printers and scanners
        E("Brother", "https://support.brother.com", "Brother"),
        E("Canon", "https://www.usa.canon.com/support", "Canon"),
        E("Epson", "https://epson.com/support", "Epson"),
        E("Xerox", "https://www.support.xerox.com", "Xerox"),
        E("Ricoh", "https://support.ricoh.com", "Ricoh"),
        E("Lexmark", "https://support.lexmark.com", "Lexmark"),
        E("Zebra", "https://www.zebra.com/us/en/support-downloads.html", "Zebra"),

        // Storage
        E("Seagate", "https://www.seagate.com/support/downloads/", "Seagate"),
        E("Western Digital", "https://support-en.wd.com", "Western Digital", "WDC"),
        E("SanDisk", "https://support-en.sandisk.com", "SanDisk"),
        E("Crucial / Micron", "https://www.crucial.com/support", "Crucial", "Micron"),
        E("Kingston", "https://www.kingston.com/en/support", "Kingston"),

        // Networking and infrastructure
        E("TP-Link", "https://www.tp-link.com/support/download/", "TP-Link", "TP Link"),
        E("NETGEAR", "https://www.netgear.com/support/", "NETGEAR"),
        E("D-Link", "https://support.dlink.com", "D-Link"),
        E("Cisco", "https://www.cisco.com/c/en/us/support/index.html", "Cisco"),
        E("Fortinet", "https://support.fortinet.com", "Fortinet", "FortiClient"),
        E("Citrix", "https://www.citrix.com/downloads/", "Citrix"),
        E("APC", "https://www.apc.com/us/en/support/", "APC", "American Power Conversion"),
    };

    /// <summary>The vendor named earliest in the title, so "Lenovo Ltd. Firmware" is Lenovo, not a later mention.</summary>
    public static DriverVendor? Find(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        DriverVendor? best = null;
        var bestAt = int.MaxValue;
        foreach (var e in Table)
        {
            var m = e.Pattern.Match(title);
            if (m.Success && m.Index < bestAt) { best = e.Vendor; bestAt = m.Index; }
        }
        return best;
    }

    /// <summary>
    /// The maker of this PC, from its manufacturer and model strings (the BIOS values Windows records), or null for
    /// virtual machines and makers not in the table. Drivers from component makers usually ship through this company.
    /// </summary>
    public static DriverVendor? ForMachine(string? manufacturer, string? model) =>
        Find($"{manufacturer} {model}".Trim());

    private static readonly Lazy<DriverVendor?> ThisMachine = new(() =>
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return ForMachine(key?.GetValue("SystemManufacturer") as string, key?.GetValue("SystemProductName") as string);
        }
        catch { return null; }
    });

    /// <summary>The maker of the PC PatchRadar is running on, read once.</summary>
    public static DriverVendor? Machine => ThisMachine.Value;

    /// <summary>True only for a URL that is in the table, so a vendor link can never be an arbitrary address.</summary>
    public static bool IsKnownUrl(string? url) =>
        url is not null && Table.Any(e => e.Vendor.SupportUrl.Equals(url, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<DriverVendor> All => Table.Select(e => e.Vendor);
}
