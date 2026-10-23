using System.Text.Json.Serialization;
using PatchRadar.Kb;

namespace PatchRadar.Core;

public enum Severity { Green, Orange, Red, Grey }

/// <summary>Pending = offered by Windows Update but not installed yet.</summary>
public enum UpdateState { Installed, Failed, Uninstalled, Pending }

public enum UpdateCategory { Security, Cumulative, DotNet, ServicingStack, Driver, Office, Definition, Other }

public enum KbStatus { Ok, Blocked, Failed, NotFound }

public enum KbStructure { Standard, Unknown }

public enum IssueStatus { Active, Resolved }

public sealed record InstalledUpdate(
    string Kb,
    string Title,
    DateTimeOffset InstalledOn,
    UpdateState State,
    UpdateCategory Category,
    string? SupportUrl = null,
    string Source = "WUA",
    int HResult = 0)
{
    /// <summary>False for updates Microsoft publishes no KB article for (most drivers). Their key is a hash of the title.</summary>
    public bool HasKb => Kb.StartsWith("KB", StringComparison.OrdinalIgnoreCase);

    /// <summary>What to show where a KB number normally goes.</summary>
    public string Label => HasKb ? Kb : "No KB";
}

public sealed class KbImprovement
{
    public string? Category { get; set; }
    public string Text { get; set; } = "";
    public List<string> Links { get; set; } = new();
}

public sealed class KbIssue
{
    public string? Title { get; set; }
    public string? AppliesTo { get; set; }
    public string Symptom { get; set; } = "";
    public string Workaround { get; set; } = "";
    /// <summary>Microsoft's stated resolution (a fix, an update to install). Distinct from a workaround.</summary>
    public string Resolution { get; set; } = "";
    /// <summary>"Next steps" text some articles give once an issue is mitigated or fixed.</summary>
    public string NextSteps { get; set; } = "";
    /// <summary>KB that fixes this issue, when the text or release health names one.</summary>
    public string? ResolvedInKb { get; set; }
    /// <summary>The fix ships as an out-of-band update rather than the regular monthly release.</summary>
    public bool OutOfBandFix { get; set; }
    /// <summary>The named fix only partly resolves the issue.</summary>
    public bool PartialFix { get; set; }
    public IssueStatus Status { get; set; } = IssueStatus.Active;
}

public sealed class KbData
{
    public string Kb { get; set; } = "";
    public DateTime FetchedUtc { get; set; }
    public string Source { get; set; } = "";
    public string? CanonicalUrl { get; set; }
    public KbStatus Status { get; set; } = KbStatus.Ok;
    public KbStructure Structure { get; set; } = KbStructure.Standard;
    public string? Title { get; set; }
    public string? OsBuild { get; set; }
    public string? ReleaseDate { get; set; }
    public string? AppliesTo { get; set; }
    public List<KbImprovement> Improvements { get; set; } = new();
    public List<KbIssue> KnownIssues { get; set; } = new();
    public string RawText { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string? LastError { get; set; }
    /// <summary>Shown in the detail window when the data is partial (for example release-health fallback).</summary>
    public string? Notice { get; set; }
    /// <summary>The previous reading of this article, kept when its content changed (for before-and-after and the digest).</summary>
    public KbSnapshot? Previous { get; set; }
    /// <summary>When the content last changed between two fetches (null if it never has).</summary>
    public DateTime? ChangedUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime? NextRetryUtc { get; set; }
}

public sealed class SeverityResult
{
    public Severity Severity { get; init; }
    public IReadOnlyList<string> MatchedKeywords { get; init; } = Array.Empty<string>();
    public IReadOnlyList<KbImprovement> BehaviourChanges { get; init; } = Array.Empty<KbImprovement>();
    public IReadOnlyList<KbIssue> ActiveIssues { get; init; } = Array.Empty<KbIssue>();
    public string? Summary { get; init; }
}
