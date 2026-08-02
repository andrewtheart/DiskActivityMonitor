using System.Diagnostics;
using System.Text;

namespace DiskActivityMonitor.Tests;

public sealed class SecureDirectoryHelperTests
{
    [Fact]
    public async Task Helper_AppliesProtectedDaclWithNoFollowHandle()
    {
        string probePath = Path.Combine(Path.GetTempPath(), $"dam_acl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(probePath, "child"));
        await File.WriteAllTextAsync(Path.Combine(probePath, "child", "probe.txt"), "probe");

        try
        {
            string helperPath = Path.Combine(FindRepositoryRoot(), "scripts", "secure-directory.ps1");
            string command = """
                $ErrorActionPreference = 'Stop'
                $source = [IO.File]::ReadAllText($env:DAM_HELPER_PATH)
                $match = [regex]::Match(
                    $source,
                    "Add-Type -TypeDefinition @'\r?\n(?<code>.*?)\r?\n'@",
                    [Text.RegularExpressions.RegexOptions]::Singleline)
                if (-not $match.Success) { throw 'Could not extract helper C#.' }
                Add-Type -TypeDefinition $match.Groups['code'].Value
                $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
                $sddl = "O:${sid}G:${sid}D:P(A;OICI;FA;;;${sid})(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"
                [DiskActivityMonitor.Security.DirectoryHandleSecurity]::Apply($env:DAM_PROBE_PATH, $sddl)
                $acl = [IO.Directory]::GetAccessControl($env:DAM_PROBE_PATH)
                if (-not $acl.AreAccessRulesProtected) { throw 'The resulting DACL is not protected.' }
                "owner=$($acl.Owner); protected=$($acl.AreAccessRulesProtected)"
                """;

            string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(encodedCommand);
            startInfo.Environment["DAM_HELPER_PATH"] = helperPath;
            startInfo.Environment["DAM_PROBE_PATH"] = probePath;

            using Process process = Process.Start(startInfo)!;
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            string output = await standardOutput;
            string error = await standardError;
            Assert.True(process.ExitCode == 0,
                $"Helper exited with {process.ExitCode}. Output: {output} Error: {error}");
            Assert.Contains("protected=True", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(probePath, recursive: true);
        }
    }

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