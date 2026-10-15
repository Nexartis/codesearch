using System.Diagnostics;

namespace ScipCsharp;

/// <summary>
/// Restore policy for the design-time workspace load: MSBuildWorkspace does
/// not restore, but running <c>dotnet restore</c> on every reindex is far too
/// heavy for multi-repo hubs. Restore only when the evidence says assets are
/// missing or stale. Compiled into both scip-csharp and the test project via
/// a linked Compile item (same pattern as WorkspaceLoad.cs).
/// </summary>
internal static class RestorePolicy
{
    /// <summary>Env override for the restore kill timeout, in seconds.</summary>
    public const string TimeoutEnvName = "CODESEARCH_RESTORE_TIMEOUT_SECS";
    private const string RestoreTimeoutEnv = TimeoutEnvName;
    private const int DefaultRestoreTimeoutSeconds = 300;

    /// <summary>
    /// True when at least one project lacks <c>obj/project.assets.json</c>, has
    /// one older than its .csproj (a package reference or target framework was
    /// edited since the last restore), or has one older than an applicable
    /// shared restore input (see <see cref="AnySharedInputNewerThan"/>). False
    /// means every project has fresh assets and restore can be skipped entirely
    /// — the steady state of an actively developed repo.
    /// </summary>
    public static bool NeedsRestore(IEnumerable<string> projectRelativePaths, string solutionDir)
    {
        foreach (var relative in projectRelativePaths)
        {
            var projectPath = Path.GetFullPath(Path.Combine(solutionDir, relative));
            var projectDir = Path.GetDirectoryName(projectPath) ?? solutionDir;
            var assetsPath = Path.Combine(projectDir, "obj", "project.assets.json");
            if (!File.Exists(assetsPath))
            {
                return true;
            }
            var assetsWritten = File.GetLastWriteTimeUtc(assetsPath);
            if (File.GetLastWriteTimeUtc(projectPath) > assetsWritten)
            {
                return true;
            }
            if (AnySharedInputNewerThan(projectDir, assetsWritten))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when a shared restore input that applies to
    /// <paramref name="projectDir"/> was modified after the assets were written.
    /// MSBuild/NuGet discover these by walking up from the project directory,
    /// and editing one changes the resolved dependency graph without touching
    /// the .csproj — under central package management or imported build config
    /// a .csproj-only comparison would treat the assets as fresh, skip restore,
    /// and load a stale asset graph. Absent files never trigger a restore.
    /// </summary>
    private static bool AnySharedInputNewerThan(string projectDir, DateTime assetsWritten)
    {
        // Per-project lock file sits beside the .csproj.
        if (IsNewerThan(Path.Combine(projectDir, PackagesLockName), assetsWritten))
        {
            return true;
        }

        // Directory.Build.props / Directory.Packages.props: MSBuild and NuGet
        // import the NEAREST one found walking up, so once a name is matched a
        // higher directory can no longer supply it.
        var nearestPending = new HashSet<string>(NearestInputNames, StringComparer.OrdinalIgnoreCase);

        for (var dir = new DirectoryInfo(projectDir); dir is not null; dir = dir.Parent)
        {
            // NuGet.config files are merged from every ancestor directory.
            if (IsNewerThan(Path.Combine(dir.FullName, NuGetConfigName), assetsWritten))
            {
                return true;
            }

            foreach (var name in NearestInputNames)
            {
                if (!nearestPending.Contains(name) || !File.Exists(Path.Combine(dir.FullName, name)))
                {
                    continue;
                }
                nearestPending.Remove(name);
                if (IsNewerThan(Path.Combine(dir.FullName, name), assetsWritten))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsNewerThan(string path, DateTime reference) =>
        File.Exists(path) && File.GetLastWriteTimeUtc(path) > reference;

    private const string NuGetConfigName = "NuGet.config";
    private const string PackagesLockName = "packages.lock.json";

    private static readonly string[] NearestInputNames =
    {
        "Directory.Packages.props",
        "Directory.Build.props",
    };

    /// <summary>
    /// Restore wait timeout in seconds: <c>CODESEARCH_RESTORE_TIMEOUT_SECS</c>
    /// when set to a positive integer, otherwise 300. Unparseable values fall
    /// back to the default rather than failing the load.
    /// </summary>
    public static int RestoreTimeoutSeconds()
    {
        var raw = Environment.GetEnvironmentVariable(RestoreTimeoutEnv);
        if (int.TryParse(raw, out var seconds) && seconds > 0)
        {
            return seconds;
        }
        return DefaultRestoreTimeoutSeconds;
    }

    /// <summary>
    /// The <c>[WARN]</c> line emitted when the bounded wait expires while
    /// <c>dotnet restore</c> is still running. The restore is NOT killed: it
    /// keeps running in the background (the child outlives the helper), this
    /// load proceeds degraded, and the next reindex finds the fresh assets —
    /// a killed restore would waste all of its work and re-pay the same wait
    /// on every subsequent reindex. Pinned by test so a "killed" wording
    /// cannot sneak back in.
    /// </summary>
    public static string StillRunningWarning(int seconds) =>
        $"[WARN] dotnet restore still running after {seconds}s — proceeding without it " +
        "(degraded symbols this run); it keeps running in the background and the next " +
        "reindex picks up the fresh assets. " +
        $"Raise with {TimeoutEnvName} if this solution legitimately needs longer.";
}
