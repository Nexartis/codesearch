namespace ScipCsharp;

/// <summary>
/// Classifies workspace-load noise that must not surface as index warnings.
/// Compiled into both scip-csharp and the test project via a linked Compile
/// item (same pattern as WorkspaceLoad.cs).
/// </summary>
internal static class WorkspaceNoise
{
    /// <summary>
    /// True when a workspace-failure diagnostic is a NuGet audit advisory
    /// ("Package 'X' has a known high severity vulnerability, ...") rather
    /// than a load failure — emitting it would pin the repo's index warning
    /// until the package is bumped, while the index itself is unaffected.
    /// Pure and null-tolerant so it is unit-testable.
    /// </summary>
    public static bool IsNuGetAuditNoise(string? message) =>
        message is not null
        && message.Contains("has a known", StringComparison.OrdinalIgnoreCase)
        && message.Contains("vulnerability", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a workspace-failure diagnostic is a NuGet package-version
    /// mismatch ("Dependency specified was 'X (&gt;= A)' but ended up with
    /// 'X B'"): the affected project still loads and its symbol tree is
    /// complete, so nothing here means "find-impact may miss symbols" — the
    /// build owner settles the pin, and the index must not carry the noise.
    /// Pure and null-tolerant so it is unit-testable.
    /// </summary>
    public static bool IsPackageVersionConflict(string? message) =>
        message is not null
        && message.Contains("Dependency specified was", StringComparison.OrdinalIgnoreCase)
        && message.Contains("ended up with", StringComparison.OrdinalIgnoreCase);
}
