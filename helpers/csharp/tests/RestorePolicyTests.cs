using ScipCsharp;
using Xunit;

namespace ScipCsharp.Tests;

/// <summary>
/// Unit tests for the restore gating policy: a repo whose projects all carry
/// fresh obj/project.assets.json must skip restore entirely (the steady state
/// of an actively developed repo), while a missing or stale assets file
/// triggers it.
/// </summary>
public class RestorePolicyTests : IDisposable
{
    private readonly string _solutionDir;

    public RestorePolicyTests()
    {
        _solutionDir = Path.Combine(Path.GetTempPath(), "cs-restore-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_solutionDir);
    }

    public void Dispose()
    {
        Directory.Delete(_solutionDir, recursive: true);
    }

    private string AddProject(string relativePath, bool withAssets, bool staleAssets = false)
    {
        var projectPath = Path.Combine(_solutionDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        if (withAssets)
        {
            var objDir = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj");
            Directory.CreateDirectory(objDir);
            var assetsPath = Path.Combine(objDir, "project.assets.json");
            File.WriteAllText(assetsPath, "{}");
            if (staleAssets)
            {
                // Make the assets look OLDER than the project file.
                File.SetLastWriteTimeUtc(assetsPath, File.GetLastWriteTimeUtc(projectPath).AddMinutes(-5));
            }
        }
        return projectPath;
    }

    [Fact]
    public void FreshAssetsOnEveryProjectSkipsRestore()
    {
        AddProject(@"src\Acme.Core\Acme.Core.csproj", withAssets: true);
        AddProject(@"src\Acme.Web\Acme.Web.csproj", withAssets: true);

        Assert.False(RestorePolicy.NeedsRestore(
            new[] { @"src\Acme.Core\Acme.Core.csproj", @"src\Acme.Web\Acme.Web.csproj" }, _solutionDir));
    }

    [Fact]
    public void MissingAssetsTriggersRestore()
    {
        AddProject(@"src\Acme.Core\Acme.Core.csproj", withAssets: true);
        AddProject(@"src\Acme.Web\Acme.Web.csproj", withAssets: false);

        Assert.True(RestorePolicy.NeedsRestore(
            new[] { @"src\Acme.Core\Acme.Core.csproj", @"src\Acme.Web\Acme.Web.csproj" }, _solutionDir));
    }

    [Fact]
    public void StaleAssetsOlderThanProjectTriggerRestore()
    {
        AddProject(@"src\Acme.Core\Acme.Core.csproj", withAssets: true, staleAssets: true);

        Assert.True(RestorePolicy.NeedsRestore(new[] { @"src\Acme.Core\Acme.Core.csproj" }, _solutionDir));
    }

    /// <summary>
    /// Central package management / imported build config can change the
    /// resolved dependency graph without touching any .csproj, so a shared
    /// input newer than the assets must force a restore even when every .csproj
    /// is older than its obj/project.assets.json.
    /// </summary>
    [Fact]
    public void StaleSharedRestoreInputTriggersRestore()
    {
        var projectPath = AddProject(@"src\Acme.Core\Acme.Core.csproj", withAssets: true);
        var assetsPath = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json");

        // A Directory.Packages.props at the solution root, newer than the assets.
        var packagesProps = Path.Combine(_solutionDir, "Directory.Packages.props");
        File.WriteAllText(packagesProps, "<Project />");
        File.SetLastWriteTimeUtc(packagesProps, File.GetLastWriteTimeUtc(assetsPath).AddMinutes(5));

        Assert.True(RestorePolicy.NeedsRestore(new[] { @"src\Acme.Core\Acme.Core.csproj" }, _solutionDir));
    }

    [Fact]
    public void EmptyProjectListSkipsRestore()
    {
        Assert.False(RestorePolicy.NeedsRestore(Array.Empty<string>(), _solutionDir));
    }

    [Fact]
    public void RestoreTimeoutDefaultsTo300AndParsesEnvOverride()
    {
        Assert.Equal(300, RestorePolicy.RestoreTimeoutSeconds());

        Environment.SetEnvironmentVariable(RestorePolicy.TimeoutEnvName, "600");
        try
        {
            Assert.Equal(600, RestorePolicy.RestoreTimeoutSeconds());
        }
        finally
        {
            Environment.SetEnvironmentVariable(RestorePolicy.TimeoutEnvName, null);
        }

        Environment.SetEnvironmentVariable(RestorePolicy.TimeoutEnvName, "not-a-number");
        try
        {
            Assert.Equal(300, RestorePolicy.RestoreTimeoutSeconds());
        }
        finally
        {
            Environment.SetEnvironmentVariable(RestorePolicy.TimeoutEnvName, null);
        }
    }

    /// <summary>
    /// Pins the timeout contract: the wait expiring must announce a restore
    /// that is STILL RUNNING in the background, never a killed one — a kill
    /// would waste all of the restore's work and re-pay the same wait on
    /// every subsequent reindex (the poison loop for the biggest solutions).
    /// </summary>
    [Fact]
    public void TimeoutWarningAnnouncesBackgroundContinuationNeverAKill()
    {
        var warning = RestorePolicy.StillRunningWarning(300);

        Assert.StartsWith("[WARN]", warning);
        Assert.Contains("still running after 300s", warning);
        Assert.Contains("background", warning);
        Assert.Contains("next", warning);
        Assert.Contains("fresh assets", warning);
        Assert.DoesNotContain("killed", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(RestorePolicy.TimeoutEnvName, warning);
    }
}
