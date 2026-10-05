using System.Diagnostics;

namespace AP_Atlas.Core.Tests;

/// <summary>A new, empty folder under the system temp folder, deleted afterwards. Tests never touch real data.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "atlas-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { } // a file still open: the system temp folder is cleaned up eventually
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>What the tests use from this computer and the repository.</summary>
internal static class TestEnvironment
{
    /// <summary>A Python on this computer that runs, or null (the Windows Store alias exists even without Python).</summary>
    public static async Task<string?> PythonAsync(CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo("python") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "-c", "import sys; print(sys.executable)" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info);
            if (process == null) return null;
            string path = (await process.StandardOutput.ReadToEndAsync(ct)).Trim();
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
            return process.ExitCode == 0 && File.Exists(path) ? path : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // no python on the PATH
        }
    }

    /// <summary>A file of the repository, found from the tests' folder upwards.</summary>
    public static string RepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = System.IO.Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Couldn't find " + System.IO.Path.Combine(parts) + " above the test folder.");
    }
}

/// <summary>What the tests save with SafeFile.</summary>
public sealed class Sample
{
    public int Version { get; set; }
    public List<string> Items { get; set; } = new();
}
