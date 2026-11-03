using PatchRadar.Updates;

namespace PatchRadar.Tests;

public class DriverVendorTests
{
    [Theory]
    [InlineData("Goodix Biometric Driver Update (3.4.51.3020)", "Goodix")]
    [InlineData("Lenovo Ltd. Firmware Driver Update (1.29.0.0)", "Lenovo")]
    [InlineData("Intel(R) Corporation - Net - 23.5.2.1", "Intel")]
    [InlineData("Realtek Semiconductor Corp. - MEDIA - 6.0.9", "Realtek")]
    [InlineData("HP Inc. - Firmware - 1.2.3", "HP")]
    [InlineData("Dell Inc. - Firmware - 2.0", "Dell")]
    public void Finds_vendor_from_title(string title, string vendor) =>
        Assert.Equal(vendor, DriverVendors.Find(title)?.Name);

    [Fact]
    public void Earliest_vendor_wins() =>
        Assert.Equal("Lenovo", DriverVendors.Find("Lenovo - Intel Management Engine - 1.0")?.Name);

    [Theory]
    [InlineData("Some Unknown Widget Driver (1.0.0.0)")]
    [InlineData("Shipping Driver Update")]   // "hp" inside a word must not match
    [InlineData("")]
    public void No_vendor_when_none_named(string title) => Assert.Null(DriverVendors.Find(title));

    [Fact]
    public void Every_url_is_https_and_known()
    {
        foreach (var v in DriverVendors.All)
        {
            Assert.StartsWith("https://", v.SupportUrl);
            Assert.True(DriverVendors.IsKnownUrl(v.SupportUrl));
        }
    }

    [Fact]
    public void Unknown_url_is_refused()
    {
        Assert.False(DriverVendors.IsKnownUrl("https://evil.example/intel"));
        Assert.False(DriverVendors.IsKnownUrl(null));
    }

    [Theory]
    [InlineData("LENOVO", "20XW", "Lenovo")]
    [InlineData("Dell Inc.", "Latitude 7440", "Dell")]
    [InlineData("HP", "HP EliteBook 840", "HP")]
    [InlineData("Microsoft Corporation", "Surface Laptop 5", "Microsoft Surface")]
    [InlineData("ASUSTeK COMPUTER INC.", "ROG", "ASUS")]
    public void Finds_pc_maker(string maker, string model, string expected) =>
        Assert.Equal(expected, DriverVendors.ForMachine(maker, model)?.Name);

    [Theory]
    [InlineData("VMware, Inc.", "VMware Virtual Platform")]
    [InlineData("Microsoft Corporation", "Virtual Machine")]
    [InlineData(null, null)]
    public void No_maker_for_virtual_machines(string? maker, string? model) =>
        Assert.Null(DriverVendors.ForMachine(maker, model));

    [Theory]
    [InlineData("Synaptics Touchpad Driver (19.5.0.0)", "Synaptics")]
    [InlineData("Poly Studio Firmware (1.0.0.0)", "Poly")]
    [InlineData("Dolby Audio Driver (1.0.0.0)", "Dolby")]
    public void Newly_added_vendors(string title, string vendor) =>
        Assert.Equal(vendor, DriverVendors.Find(title)?.Name);
}
