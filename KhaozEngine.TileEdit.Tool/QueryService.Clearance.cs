using System.Linq;

namespace KhaozEngine.TileEdit;

/// <summary>The clearance query surface.</summary>
public sealed partial class QueryService
{
    /// <summary>The world's clearance block, or null when it declares none.</summary>
    public ClearanceInfo? ClearanceGet() => session.Read(e =>
        e.Document.Clearance is { } settings ? ClearanceInfo.Of(settings) : null);

    /// <summary>Runs the clearance check against the meshes on disk without writing anything.</summary>
    public ClearanceCheckResult ClearanceCheck()
    {
        string[] issues = session.ClearanceIssues().Select(i => $"[{i.Code}] {i.Message}").ToArray();
        return new ClearanceCheckResult(issues.Length == 0, issues);
    }
}
