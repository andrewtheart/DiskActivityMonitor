using System.Diagnostics;

namespace DiskActivityMonitor.Tests;

public sealed class InstallerScriptTests
{
    [Fact]
    public void CanonicalScript_RemainsBuildAllInstallers()
    {
        string canonical = ReadScript("scripts", "build-all-installers.ps1");

        Assert.Contains("Canonical installer workflow for Disk Activity Monitor.", canonical);
        Assert.Contains("installer\\build-installer.ps1", canonical);
        Assert.Contains("Invoke-DamFocusedPendingCommits", canonical);
    }

    [Fact]
    public void InstallerScript_PushDelegatesToCanonicalWorkflow()
    {
        string installer = ReadScript("installer", "build-installer.ps1");

        Assert.Contains("Delegating -Push workflow to scripts\\build-all-installers.ps1", installer);
        Assert.Contains("$canonicalScript = Join-Path $root 'scripts\\build-all-installers.ps1'", installer);
        Assert.Contains("& $canonicalScript @delegateArgs", installer);
        Assert.DoesNotContain("=== Publishing GitHub release ===", installer);
        Assert.DoesNotContain("gh release create", installer);
    }

    [Fact]
    public void CanonicalScript_UsesFailFastGitPreflightAndReviewedWholeFileAtomicPlanBeforeBuild()
    {
        string canonical = ReadScript("scripts", "build-all-installers.ps1");
        string helper = ReadScript("scripts", "installer-git-commits.ps1");

        Assert.Contains("Invoke-DamGitPreflight", helper);
        Assert.Contains("MERGE_HEAD", helper);
        Assert.Contains("CHERRY_PICK_HEAD", helper);
        Assert.Contains("REVERT_HEAD", helper);
        Assert.Contains("rebase-merge", helper);
        Assert.Contains("rebase-apply", helper);
        Assert.DoesNotContain("REBASE_HEAD", helper);
        Assert.Contains("Renamed/copied paths must be handled manually", helper);
        Assert.Contains("Dirty noninteractive/CI runs are blocked", helper);
        Assert.Contains("Resolve-DamCopilotExecutable", helper);
        Assert.Contains("Invoke-DamCopilotWholeFilePlanner", helper);
        Assert.Contains("Assert-DamCompleteWholeFileCommitPlan", helper);
        Assert.Contains("return $normalizedGroups.ToArray()", helper);
        Assert.Contains("Test-DamWholeFilePlanWithTemporaryIndex", helper);
        Assert.Contains("Show-DamWholeFileCommitPlan", helper);
        Assert.Contains("Invoke-DamExecuteWholeFileCommitPlan", helper);
        Assert.Contains("Apply this whole-file commit plan? [yes/abort]", helper);
        Assert.Contains("Whole-file commit plan declined; no plan was applied.", helper);
        Assert.Contains("Staged path set does not exactly match planned group paths.", helper);
        Assert.Contains("Copilot commit plan is invalid", helper);
        Assert.Contains("Copilot whole-file commit planning is unavailable", helper);
        Assert.Contains("DEFERRED: Interactive hunk workflow retained for possible future restoration", helper);
        Assert.Contains("& git --no-pager -C $RepoRoot add --patch", helper);
        Assert.DoesNotContain("Use git add --patch to stage exactly one functional group", helper);
        Assert.Contains("Invoke-DamReviewedStagedCommit", helper);
        Assert.Contains("Invoke-DamFocusedPendingCommits -RepoRoot $repoRoot -CopilotExecutable $plannerCopilotExecutable", canonical);
    }

    [Fact]
    public void CanonicalScript_UsesExplicitAllowlistForPostBuildGeneratedTrackedFiles()
    {
        string canonical = ReadScript("scripts", "build-all-installers.ps1");
        string helper = ReadScript("scripts", "installer-git-commits.ps1");

        Assert.Contains("Invoke-DamReleaseGeneratedCommit", canonical);
        Assert.Contains("AllowedTrackedPaths @('assets/app.ico')", canonical);
        Assert.Contains("Unexpected post-build tracked change(s)", helper);
        Assert.Contains("Staged path set does not exactly match allowlisted release-generated files", helper);
    }

    [Fact]
    public void CanonicalScript_WhatIfDescribesVersionBuildCommitAndReleaseVerificationPlan()
    {
        string canonical = ReadScript("scripts", "build-all-installers.ps1");

        Assert.Contains("WhatIf: canonical installer plan", canonical);
        Assert.Contains("pre-build commit strategy", canonical);
        Assert.Contains("whole-file atomic Copilot plan", canonical);
        Assert.DoesNotContain("reviewed interactive hunk commits", canonical);
        Assert.Contains("build command: installer\\build-installer.ps1", canonical);
        Assert.Contains("release action:", canonical);
        Assert.Contains("verification plan: gh release view", canonical);
    }

    [Fact]
    public void CanonicalScript_RequiresGhAndCopilotForNewReleaseButNotPushSkipRelease()
    {
        string canonical = ReadScript("scripts", "build-all-installers.ps1");
        string helper = ReadScript("scripts", "installer-git-commits.ps1");

        Assert.Contains("GitHub CLI (gh) is required for -Push unless -SkipRelease is supplied.", canonical);
        Assert.Contains("gh is not authenticated", canonical);
        Assert.Contains("Resolve-CopilotCliPath", canonical);
        Assert.Contains("Assert-CopilotAuthenticated", canonical);
        Assert.Contains("if ($Push -and -not $SkipRelease)", canonical);
        Assert.Contains("& $CopilotCliPath --version", canonical);
        Assert.Contains("Invoke-DamCopilotPrompt -RepoRoot $repoRoot -CopilotExecutable $CopilotCliPath -Prompt $PromptText", canonical);
        Assert.Contains("'--no-ask-user'", helper);
        Assert.DoesNotContain("--prompt-file", canonical);
        Assert.DoesNotContain("& $CopilotCliPath auth status", canonical);
        Assert.Contains("Resolve-DamCopilotExecutable", helper);
        Assert.Contains("Get-Command copilot.exe -CommandType Application", helper);
        Assert.Contains("Native copilot.exe is required for whole-file commit planning", helper);
        Assert.Contains("RedirectStandardOutput = $true", helper);
        Assert.Contains("RedirectStandardError = $true", helper);
        Assert.Contains("StandardOutputEncoding = $utf8NoBom", helper);
        Assert.Contains("Invoke-DamCopilotPrompt -RepoRoot $RepoRoot -CopilotExecutable $CopilotExecutable -Prompt $prompt", helper);
        Assert.Contains("$maxInlineContextChars = 16000", helper);
        Assert.Contains("Planning context was truncated to stay within the Windows command-line limit", helper);
        Assert.Contains("return Resolve-DamCopilotExecutable -ProvidedPath $ProvidedPath", canonical);
        Assert.Contains("if ([string]::IsNullOrWhiteSpace($plannerCopilotExecutable))", canonical);
    }

    [Fact]
    public void CopilotResolver_PrefersNativeExecutableOverPowerShellShim()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "dam_copilot_resolver_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string shimPath = Path.Combine(tempDirectory, "copilot.ps1");
            string executablePath = Path.Combine(tempDirectory, "copilot.exe");
            File.WriteAllText(shimPath, "throw 'The shim must not run.'");
            File.WriteAllBytes(executablePath, []);

            string helperPath = ReadScriptPath("scripts", "installer-git-commits.ps1");
            string command = "$env:PATH = '" + tempDirectory.Replace("'", "''") + ";' + $env:PATH; "
                + ". '" + helperPath.Replace("'", "''") + "'; "
                + "$implicit = Resolve-DamCopilotExecutable; "
                + "$explicit = Resolve-DamCopilotExecutable -ProvidedPath '" + shimPath.Replace("'", "''") + "'; "
                + "@($implicit, $explicit) | ForEach-Object { [IO.Path]::GetFullPath($_) }";
            string encodedCommand = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));

            using Process process = new();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encodedCommand,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();
            string stdOut = process.StandardOutput.ReadToEnd();
            string stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, stdOut + Environment.NewLine + stdErr);
            string[] resolvedPaths = stdOut.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, resolvedPaths.Length);
            Assert.All(resolvedPaths, path => Assert.Equal(executablePath, path, ignoreCase: true));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void CapturedProcessFallback_QuotesWindowsArguments()
    {
        string helperPath = ReadScriptPath("scripts", "installer-git-commits.ps1");
        string command = ". '" + helperPath.Replace("'", "''") + "'; "
            + "ConvertTo-DamWindowsCommandLineArgument -Argument 'alpha beta'; "
            + "ConvertTo-DamWindowsCommandLineArgument -Argument 'alpha\"beta'; "
            + "ConvertTo-DamWindowsCommandLineArgument -Argument 'alpha beta\\'";
        string encodedCommand = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));

        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encodedCommand,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        process.Start();
        string stdOut = process.StandardOutput.ReadToEnd();
        string stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, stdOut + Environment.NewLine + stdErr);
        Assert.Equal(
            ["\"alpha beta\"", "\"alpha\\\"beta\"", "\"alpha beta\\\\\""],
            stdOut.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void CanonicalScript_InjectsSingleVersionIntoPublishOutputsAndInstallerNames()
    {
        string canonical = ReadScript("scripts", "build-all-installers.ps1");
        string installer = ReadScript("installer", "build-installer.ps1");

        Assert.Contains("-Version $resolvedVersion", canonical);
        Assert.Contains("DiskActivityMonitor-Setup-$resolvedVersion-", canonical);
        Assert.Contains("-p:Version=$Version", installer);
        Assert.Contains("-p:InformationalVersion=$Version", installer);
        Assert.Contains("-p:FileVersion=$fileVersion", installer);
        Assert.Contains("$fileVersion = (($Version -split '[-+]')[0] + '.0')", installer);
    }

    [Fact]
    public void CanonicalScript_ValidatesExactExpectedAssetsAndFreshnessWithoutWildcardRediscovery()
    {
        string canonical = ReadScript("scripts", "build-all-installers.ps1");

        Assert.Contains("$buildStartUtc = [DateTime]::UtcNow", canonical);
        Assert.Contains("$installerPath = Join-Path $installerOutput", canonical);
        Assert.Contains("Expected installer was not produced", canonical);
        Assert.Contains("Expected installer is empty", canonical);
        Assert.Contains("Expected installer is not fresh for this invocation", canonical);
        Assert.Contains("InstallerFile = $fileInfo", canonical);
    }

    [Fact]
    public void CanonicalScript_GeneratesComprehensiveCopilotNotesAndDeterministicSections()
    {
        string canonical = ReadScript("scripts", "build-all-installers.ps1");

        Assert.Contains("New-ReleaseNotesFromCopilot", canonical);
        Assert.Contains("## What's changed", canonical);
        Assert.Contains("## Installation", canonical);
        Assert.Contains("Set-MarkedSection", canonical);
        Assert.Contains("DAM_ASSETS", canonical);
        Assert.Contains("DAM_VALIDATION", canonical);
        Assert.Contains("DAM_CHANGELOG", canonical);
        Assert.Contains("Build-DeterministicReleaseBody", canonical);
    }

    [Fact]
    public void CanonicalScript_ProtectsTagTargetOnRefreshAndVerifiesLiveReleaseStateAssetsAndDigest()
    {
        string canonical = ReadScript("scripts", "build-all-installers.ps1");

        Assert.Contains("Get-RemoteReleaseTagTarget", canonical);
        Assert.Contains("Refusing --clobber refresh", canonical);
        Assert.Contains("release upload $tag @($releaseAssets.FullName) --clobber", canonical);
        Assert.Contains("Assert-ReleaseMatchesBuild", canonical);
        Assert.Contains("isPrerelease", canonical);
        Assert.Contains("expected exactly one asset", canonical);
        Assert.Contains("SHA-256 mismatch", canonical);
        Assert.Contains("Write-Host ([string]$release.url)", canonical);
    }

    [Fact]
    public void Scripts_ParseCleanlyUnderWindowsPowerShellParser()
    {
        AssertParsesWithWindowsPowerShell(ReadScriptPath("scripts", "installer-git-commits.ps1"));
        AssertParsesWithWindowsPowerShell(ReadScriptPath("scripts", "build-all-installers.ps1"));
        AssertParsesWithWindowsPowerShell(ReadScriptPath("installer", "build-installer.ps1"));
    }

    [Fact]
    public void Scripts_UseExplicitGitNoPagerAndAvoidBareGitInvocationPatterns()
    {
        string canonical = ReadScript("scripts", "build-all-installers.ps1");
        string helper = ReadScript("scripts", "installer-git-commits.ps1");

        string stripDeferred = @"<#[\s\S]*?#>";
        string canonicalActive = System.Text.RegularExpressions.Regex.Replace(canonical, stripDeferred, string.Empty);
        string helperActive = System.Text.RegularExpressions.Regex.Replace(helper, stripDeferred, string.Empty);

        Assert.DoesNotMatch(@"(?m)(?<![#\w-])&?\s*git\s+-C\s+", canonicalActive);
        Assert.DoesNotMatch(@"(?m)(?<![#\w-])&?\s*git\s+-C\s+", helperActive);
        Assert.DoesNotMatch(@"(?m)&\s*git\s+@(?![^\r\n]*--no-pager)", canonicalActive);
        Assert.DoesNotMatch(@"(?m)&\s*git\s+@(?![^\r\n]*--no-pager)", helperActive);

        Assert.Contains("git --no-pager -C $RepoRoot log", canonical);
        Assert.Contains("git --no-pager -C $repoRoot push", canonical);
        Assert.Contains("git --no-pager -C $RepoRoot diff --stat", canonical);
        Assert.Contains("git --no-pager -C $RepoRoot commit", helper);
    }

    [Fact]
    public void FreshSettings_IncludeDpapiProtectedAiSecrets()
    {
        string script = ReadInstallerScript();

        Assert.Contains("function AiSecretsPath(): String;", script);
        Assert.Contains("FileExists(AiSecretsPath())", script);
        Assert.Contains("DeleteRegularFile(AiSecretsPath(), ErrorText)", script);
        Assert.Contains("DeleteRegularFile(AiSecretsPath() + '.tmp', ErrorText)", script);
        Assert.Contains("remove saved API credentials", script);
    }

    [Fact]
    public void PrepareToInstall_ShowsProgressAndAccountsForEveryStep()
    {
        string script = ReadInstallerScript();

        Assert.Contains("CreateOutputProgressPage(", script);
        Assert.Contains("BeginInstallPreparation;", script);
        Assert.Contains("try", script);
        Assert.Contains("finally\n    FinishInstallPreparation;", script.Replace("\r\n", "\n"));

        const string stepCountPrefix = "InstallPreparationStepCount = ";
        int declaration = script.IndexOf(stepCountPrefix, StringComparison.Ordinal);
        Assert.True(declaration >= 0);
        int valueStart = declaration + stepCountPrefix.Length;
        int valueEnd = script.IndexOf(';', valueStart);
        int expectedSteps = int.Parse(script[valueStart..valueEnd]);
        int completedSteps = System.Text.RegularExpressions.Regex.Matches(
            script,
            @"(?m)^\s+CompleteInstallPreparationStep;\s*$").Count;

        Assert.Equal(expectedSteps, completedSteps);
    }

    private static void AssertParsesWithWindowsPowerShell(string scriptPath)
    {
        string escapedPath = scriptPath.Replace("'", "''");
        string command = "$errors = @(); [void][System.Management.Automation.Language.Parser]::ParseFile('"
            + escapedPath
            + "', [ref]$null, [ref]$errors); if ($errors.Count -gt 0) { $errors | ForEach-Object { $_.Message }; exit 1 }";

        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "\\\"") + "\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        process.Start();
        string stdOut = process.StandardOutput.ReadToEnd();
        string stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0,
            "PowerShell parser failed for " + scriptPath + Environment.NewLine + stdOut + Environment.NewLine + stdErr);
    }

    private static string ReadInstallerScript() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "installer",
        "DiskActivityMonitor.iss"));

    private static string ReadScript(params string[] relativePathSegments)
        => File.ReadAllText(ReadScriptPath(relativePathSegments));

    private static string ReadScriptPath(params string[] relativePathSegments)
        => Path.Combine(new[] { FindRepositoryRoot() }.Concat(relativePathSegments).ToArray());

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DiskActivityMonitor.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}