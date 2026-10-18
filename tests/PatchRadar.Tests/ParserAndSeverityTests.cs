using PatchRadar.Core;
using PatchRadar.Kb;

namespace PatchRadar.Tests;

public class ParserTests
{
    [Fact]
    public void Golden_Kb5127648_HasAuthenticationImprovement()
    {
        var d = Fx.Parse("kb5127648");
        Assert.Equal(KbStatus.Ok, d.Status);
        Assert.Equal("26300.9711", d.OsBuild);
        var imp = Assert.Single(d.Improvements, i => i.Category == "Authentication");
        Assert.Contains("BlockNtlmv1SSO", imp.Text);
        Assert.Contains("MS-CHAPv2", imp.Text);
    }

    [Fact]
    public void Kb5127648_ParsesDetailsStyleKnownIssues()
    {
        var d = Fx.Parse("kb5127648");
        Assert.Equal(3, d.KnownIssues.Count);
        var rdp = d.KnownIssues.Single(i => i.Title!.StartsWith("Remote Desktop"));
        Assert.Contains("0xC000000D", rdp.Symptom);
        Assert.Contains("Administrator protection", $"{rdp.Symptom} {rdp.Workaround} {rdp.Resolution} {rdp.NextSteps}");
        // "partially resolved" must not count as Resolved
        Assert.All(d.KnownIssues, i => Assert.Equal(IssueStatus.Active, i.Status));
    }

    [Fact]
    public void Kb5044281_Server2022_TableWithoutAppliesToColumn()
    {
        var d = Fx.Parse("kb5044281");
        var issue = Assert.Single(d.KnownIssues);
        Assert.Contains("OpenSSH", issue.Symptom);
        Assert.Contains("KB5053603", issue.Workaround);
        Assert.Equal("20348.2762", d.OsBuild);
    }

    [Fact]
    public void Kb5044284_Win11_TableWithAppliesTo()
    {
        var d = Fx.Parse("kb5044284");
        Assert.Equal(3, d.KnownIssues.Count);
        Assert.Equal("All users", d.KnownIssues[0].AppliesTo);
        Assert.Contains("Roblox", d.KnownIssues[0].Symptom);
        Assert.Contains("Remote Desktop Gateway Service", d.Improvements[0].Category);
    }

    [Fact]
    public void Kb5048667_Server2025Style_TableParsed()
    {
        var d = Fx.Parse("kb5048667");
        Assert.Equal(2, d.KnownIssues.Count);
        Assert.Equal("26100.2605", d.OsBuild);
    }

    [Fact]
    public void Kb5044293_Server2016_NoIssues()
    {
        var d = Fx.Parse("kb5044293");
        Assert.Empty(d.KnownIssues);
        Assert.Equal("14393.7428", d.OsBuild);
        Assert.Equal(Severity.Green, SeverityEngine.Default().Evaluate(d).Severity);
    }

    [Fact]
    public void Kb5044033_DotNetFramework_NoIssuesAndSummaryFallback()
    {
        var d = Fx.Parse("kb5044033");
        Assert.Empty(d.KnownIssues);
        Assert.Contains(d.Improvements, i => i.Text.Contains("CVE-2024-43483"));
    }

    [Fact]
    public void Kb5055523_TabGroupLayout_SymptomAndWorkaround()
    {
        var d = Fx.Parse("kb5055523");
        Assert.Equal(4, d.KnownIssues.Count);
        var citrix = d.KnownIssues.Single(i => i.Title == "Citrix");
        Assert.Contains("Session Recording Agent", citrix.Symptom);
        Assert.Contains("2503", citrix.Workaround);
        // vendor-fixed in a newer version => reported as resolved
        Assert.Equal(IssueStatus.Resolved, citrix.Status);
    }

    [Fact]
    public void EmptyShell_IsBlocked()
    {
        var d = KbParser.Parse("KB1", "<html><body><div id='app'></div><script>challenge()</script></body></html>", "t", null, DateTime.UtcNow);
        Assert.Equal(KbStatus.Blocked, d.Status);
    }

    [Fact]
    public void ArticleWithoutKbSections_IsNotBlocked_AndIsUnknownStructure()
    {
        var html = "<html><head><title>Windows Security app update | Microsoft Support</title></head><body><main><h1>Windows Security app update</h1><p>Short note.</p></main></body></html>";
        var d = KbParser.Parse("KB5007651", html, "t", null, DateTime.UtcNow);
        Assert.Equal(KbStatus.Ok, d.Status);
        Assert.Equal(KbStructure.Unknown, d.Structure);
    }

    [Fact]
    public void ContentHash_IsStableAndChangesWithIssueText()
    {
        var a = Fx.Parse("kb5044281");
        var b = Fx.Parse("kb5044281");
        Assert.Equal(a.ContentHash, b.ContentHash);
        var html = Fx.Html("kb5044281").Replace("run the sshd.exe process", "run the sshd.exe daemon");
        var c = KbParser.Parse("KB5044281", html, "t", null, DateTime.UtcNow);
        Assert.NotEqual(a.ContentHash, c.ContentHash);
    }

    [Fact]
    public void ResolvedSection_NotCountedInSeverity()
    {
        var html = """
            <html><body><main><h1>May 1, 2026—KB1 (OS Build 1.2)</h1>
            <h2>Improvements</h2><ul><li>Something minor happened here today.</li></ul>
            <h2>Known issues in this update</h2>
            <table><thead><tr><th>Symptom</th><th>Workaround</th></tr></thead>
            <tbody><tr><td>VPN connections fail. This issue has been resolved.</td><td>None</td></tr></tbody></table>
            <h2>How to get this update</h2><p>x</p></main></body></html>
            """;
        var d = KbParser.Parse("KB1", html, "t", null, DateTime.UtcNow);
        Assert.Equal(IssueStatus.Resolved, Assert.Single(d.KnownIssues).Status);
        Assert.Equal(Severity.Green, SeverityEngine.Default().Evaluate(d).Severity);
    }
}

public class SeverityTests
{
    private static KbData Kb(Action<KbData>? cfg = null)
    {
        var d = new KbData { Kb = "KB1", Status = KbStatus.Ok };
        cfg?.Invoke(d);
        return d;
    }

    [Fact]
    public void Golden_Kb5127648_IsRedWithExpectedKeywords()
    {
        var r = SeverityEngine.Default().Evaluate(Fx.Parse("kb5127648"));
        Assert.Equal(Severity.Red, r.Severity);
        Assert.Single(r.BehaviourChanges);
        foreach (var k in new[] { "NTLMv1", "MS-CHAPv2", "Wi-Fi", "VPN" })
            Assert.Contains(k, r.MatchedKeywords);
    }

    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { "null => grey", null!, Severity.Grey },
        new object[] { "failed fetch => grey", Kb(d => d.Status = KbStatus.Blocked), Severity.Grey },
        new object[] { "clean => green", Kb(), Severity.Green },
        new object[] { "non-critical active issue => orange",
            Kb(d => d.KnownIssues.Add(new KbIssue { Symptom = "Calculator colours look odd" })), Severity.Orange },
        new object[] { "critical active issue => red",
            Kb(d => d.KnownIssues.Add(new KbIssue { Symptom = "VPN connections drop" })), Severity.Red },
        new object[] { "resolved critical issue ignored => green",
            Kb(d => d.KnownIssues.Add(new KbIssue { Symptom = "VPN connections drop", Status = IssueStatus.Resolved })), Severity.Green },
        new object[] { "behaviour change with critical keyword => red",
            Kb(d => d.Improvements.Add(new KbImprovement { Text = "Kerberos is now enabled by default." })), Severity.Red },
        new object[] { "change phrase without critical keyword => green",
            Kb(d => d.Improvements.Add(new KbImprovement { Text = "The clock widget is now enabled by default." })), Severity.Green },
        new object[] { "critical keyword without change phrase => green",
            Kb(d => d.Improvements.Add(new KbImprovement { Text = "Fixed a VPN crash." })), Severity.Green },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Rules(string name, KbData? kb, Severity expected)
    {
        Assert.True(expected == SeverityEngine.Default().Evaluate(kb).Severity, name);
    }

    [Fact]
    public void TreatBehaviourChangesAsIssues_False_DisablesRedForChanges()
    {
        var engine = new SeverityEngine(SeverityOptions.Create(null, null, treat: false));
        var kb = Kb(d => d.Improvements.Add(new KbImprovement { Text = "Kerberos is now enabled by default." }));
        Assert.Equal(Severity.Green, engine.Evaluate(kb).Severity);
    }

    [Fact]
    public void CustomKeywords_ReplaceDefaults()
    {
        var engine = new SeverityEngine(SeverityOptions.Create(new[] { "frobnicator" }, null));
        var kb = Kb(d => d.KnownIssues.Add(new KbIssue { Symptom = "VPN fails; also the frobnicator breaks" }));
        var r = engine.Evaluate(kb);
        Assert.Equal(Severity.Red, r.Severity);
        Assert.Equal(new[] { "frobnicator" }, r.MatchedKeywords);
    }
}

public class KeywordMatcherTests
{
    [Fact]
    public void WordBoundary_DoesNotMatchInsideWords()
    {
        var m = new KeywordMatcher(new[] { "print", "DNS" });
        Assert.Empty(m.Match("The printer and dnsmasq are unrelated"));
        Assert.Equal(new[] { "print" }, m.Match("Print jobs fail"));
    }

    [Fact]
    public void CaseInsensitive_AndPunctuationKeywords()
    {
        var m = new KeywordMatcher(new[] { "802.1X", "Wi-Fi", "MS-CHAP" });
        Assert.Equal(new[] { "802.1X", "Wi-Fi" }, m.Match("wi-fi with 802.1x fails"));
        Assert.Empty(m.Match("uses MS-CHAPv2")); // boundary: MS-CHAP is not MS-CHAPv2
    }

    [Fact]
    public void RegexPrefix_IsHonouredAndBadRegexIgnored()
    {
        var m = new KeywordMatcher(new[] { @"re:0x8007\w+", "re:(unclosed" });
        Assert.Equal(new[] { @"re:0x8007\w+" }, m.Match("Error 0x80070005 appears"));
    }

    [Fact]
    public void Kb5127648_SeparatesResolutionFromSymptom_AndFindsTheOutOfBandFix()
    {
        var d = Fx.Parse("kb5127648");
        var usb = d.KnownIssues.Single(i => i.Title!.StartsWith("USB audio"));
        Assert.Equal("KB5129195", usb.ResolvedInKb);
        Assert.True(usb.OutOfBandFix);
        Assert.True(usb.PartialFix);                       // "partially resolved" in the OOB update
        Assert.Equal(IssueStatus.Active, usb.Status);
        Assert.Contains("out-of-band", usb.Resolution, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("out-of-band", usb.Symptom, StringComparison.OrdinalIgnoreCase);

        var sac = d.KnownIssues.Single(i => i.Title!.Contains("Smart App Control"));
        Assert.Null(sac.ResolvedInKb);                    // registry guidance only: no fix KB
        Assert.False(sac.OutOfBandFix);
        Assert.NotEmpty(sac.Resolution);
    }

    [Fact]
    public void Kb5055523_FindsAddressedInKb_AndResolvedIssueStaysResolved()
    {
        var d = Fx.Parse("kb5055523");
        var hello = d.KnownIssues.Single(i => i.Title == "Windows Hello");
        Assert.Equal("KB5055627", hello.ResolvedInKb);
        Assert.False(hello.OutOfBandFix);
        Assert.Equal(IssueStatus.Active, hello.Status);     // the fix exists but this update does not include it
        Assert.Equal(IssueStatus.Resolved, d.KnownIssues.Single(i => i.Title == "Citrix").Status);
        Assert.Null(d.KnownIssues.Single(i => i.Title == "Noto fonts issue").ResolvedInKb);
    }

    [Fact]
    public void Kb5044281_TableIssue_NamesItsFix() =>
        Assert.Equal("KB5053603", Fx.Parse("kb5044281").KnownIssues.Single().ResolvedInKb);
}
