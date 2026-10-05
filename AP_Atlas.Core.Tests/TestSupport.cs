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

/// <summary>What the tests save with SafeFile.</summary>
public sealed class Sample
{
    public int Version { get; set; }
    public List<string> Items { get; set; } = new();
}
