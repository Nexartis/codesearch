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

    private string AddRestoreInput(string relativePath, bool touched) =>
        AddRestoreInputAt(Path.Combine(_solutionDir, relativePath), touched);

    private string AddRestoreInputAt(string inputPath, bool touched)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
        File.WriteAllText(inputPath, "<Project />");
        File.SetLastWriteTimeUtc(
            inputPath,
            DateTime.UtcNow.AddMinutes(touched ? 5 : -5));
        return inputPath;
    }

    /// <summary>
    /// Central package management: bumping a version in
    /// Directory.Packages.props must mark the assets stale even though no
    /// .csproj changed — the CS0246 class of false-fresh assets.
    /// </summary>
    [Fact]
    public void TouchedPackagesPropsAtSolutionRootTriggersRestore()
    {
        AddProject(@"src\Acme.Core\Acme.Core.csproj", withAssets: true);
        AddRestoreInput(@"Directory.Packages.props", touched: true);

        Assert.True(RestorePolicy.NeedsRestore(
            new[] { @"src\Acme.Core\Acme.Core.csproj" }, _solutionDir));
    }

    [Fact]
    public void UntouchedPackagesPropsKeepsRestoreSkipped()
    {
        AddProject(@"src\Acme.Core\Acme.Core.csproj", withAssets: true);
        AddRestoreInput(@"Directory.Packages.props", touched: false);

        Assert.False(RestorePolicy.NeedsRestore(
            new[] { @"src\Acme.Core\Acme.Core.csproj" }, _solutionDir));
    }

    /// <summary>
    /// The walk must extend ABOVE the solution dir: a Directory.Build.props at
    /// the repo root applies to a solution sitting deeper in the repo.
    /// </summary>
    [Fact]
    public void InputAboveSolutionDirAtRepoRootTriggersRestore()
    {
        var repoDir = Directory.CreateDirectory(Path.Combine(_solutionDir, "repo")).FullName;
        var solutionDir = Directory.CreateDirectory(Path.Combine(repoDir, "sln")).FullName;
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));

        var projectPath = Path.Combine(solutionDir, "src", "Acme.Core", "Acme.Core.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var objDir = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj");
        Directory.CreateDirectory(objDir);
        File.WriteAllText(Path.Combine(objDir, "project.assets.json"), "{}");

        AddRestoreInputAt(Path.Combine(repoDir, "Directory.Build.props"), touched: true);

        Assert.True(RestorePolicy.NeedsRestore(
            new[] { @"src\Acme.Core\Acme.Core.csproj" }, solutionDir));
    }

    [Fact]
    public void TouchedNuGetConfigAndGlobalJsonTriggerRestore()
    {
        AddProject(@"src\Acme.Core\Acme.Core.csproj", withAssets: true);
        AddRestoreInput(@"NuGet.config", touched: true);
        AddRestoreInput(@"global.json", touched: false);

        Assert.True(RestorePolicy.NeedsRestore(
            new[] { @"src\Acme.Core\Acme.Core.csproj" }, _solutionDir));
    }

    /// <summary>
    /// Pins the case-insensitive input-name match: NuGet honours lowercase
    /// nuget.config on case-sensitive filesystems, where an exact-case probe
    /// would miss it.
    /// </summary>
    [Fact]
    public void CaseVariantInputNameStillTriggersRestore()
    {
        AddProject(@"src\Acme.Core\Acme.Core.csproj", withAssets: true);
        AddRestoreInput(@"nuget.config", touched: true);

        Assert.True(RestorePolicy.NeedsRestore(
            new[] { @"src\Acme.Core\Acme.Core.csproj" }, _solutionDir));
    }
}
