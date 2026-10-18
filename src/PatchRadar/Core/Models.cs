using System.Text.Json.Serialization;

namespace PatchRadar.Core;

public enum Severity { Green, Orange, Red, Grey }

public enum UpdateState { Installed, Failed, Uninstalled }

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
    string Source = "WUA");

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
