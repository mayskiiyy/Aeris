using System.IO;

namespace Aeris.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; }

    /// <summary>A fresh folder in %TEMP%, or in the user profile when the test needs a place outside AppData.</summary>
    public TempDir(bool outsideAppData = false)
    {
        string parent = outsideAppData ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : System.IO.Path.GetTempPath();
        Path = System.IO.Path.Combine(parent, ".aeris-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string File(string relative, string content = "x", DateTime? time = null)
    {
        string full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        if (time is { } t) { System.IO.File.SetCreationTimeUtc(full, t); System.IO.File.SetLastWriteTimeUtc(full, t); }
        return full;
    }

    public string Dir(string relative) => Directory.CreateDirectory(System.IO.Path.Combine(Path, relative)).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
            string parent = System.IO.Path.GetDirectoryName(Path)!;
            if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
