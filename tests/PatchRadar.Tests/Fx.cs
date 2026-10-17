using PatchRadar.Core;
using PatchRadar.Kb;

namespace PatchRadar.Tests;

internal static class Fx
{
    public static string Html(string kb) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "kb", $"{kb}.html"));

    public static KbData Parse(string kb) =>
        KbParser.Parse(kb.ToUpperInvariant(), Html(kb), "fixture", null, new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc));
}
