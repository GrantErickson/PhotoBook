namespace PhotoBook.Tests.Fixtures;

/// <summary>
/// A throwaway folder under the system temp directory, deleted on dispose. Every integration test
/// that touches the filesystem gets one of these, so tests never collide and never leave residue.
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    /// <summary>Creates and returns an empty workspace folder.</summary>
    /// <param name="name">A short label folded into the folder name so failures are recognizable.</param>
    public TempWorkspace(string name = "pb")
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "photobook-tests", $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    /// <summary>The absolute workspace folder.</summary>
    public string Root { get; }

    /// <summary>An absolute path inside the workspace; parent folders are created.</summary>
    public string At(params string[] parts)
    {
        var full = System.IO.Path.Combine([Root, .. parts]);
        var parent = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        return full;
    }

    /// <summary>Creates a subfolder and returns its absolute path.</summary>
    public string Folder(string name)
    {
        var full = System.IO.Path.Combine(Root, name);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(Root)) return;

            // Archived originals are marked read-only by the importer (doc 04 §7); clear the attribute
            // before deleting or the recursive delete throws.
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked file must never fail a passing test; the OS reclaims temp folders.
        }
    }
}
