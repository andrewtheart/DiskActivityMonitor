namespace DiskActivityMonitor.Tests;

public sealed class InstallerScriptTests
{
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

    private static string ReadInstallerScript() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "installer",
        "DiskActivityMonitor.iss"));

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