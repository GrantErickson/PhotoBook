using System.Globalization;
using PhotoBook.Core.Model;

namespace PhotoBook.Core.Persistence;

/// <summary>
/// The project folder layout of kernel §5 / doc 04 §2. A PhotoBook project <em>is</em> a folder:
/// self-contained, human-diffable, and openable years from now with nothing but a JSON viewer.
/// <code>
/// MyBook-2024/
///   book.json          — book settings, style, page size, print profile ref, seed
///   photos.json        — the photo catalog
///   journal.json       — parsed journal entries + the import report
///   chapters/2024-01.json … 2024-12.json
///   originals/         — copied source images, content-hash-prefixed, immutable
///   cache/             — thumbnails and analysis outputs; 100% regenerable
/// </code>
/// The app identifies a project by the presence of <c>book.json</c>. Names here are fixed.
/// </summary>
public sealed class ProjectPaths
{
    /// <summary>File name of the book document.</summary>
    public const string BookFileName = "book.json";

    /// <summary>File name of the photo catalog.</summary>
    public const string PhotosFileName = "photos.json";

    /// <summary>File name of the journal document.</summary>
    public const string JournalFileName = "journal.json";

    /// <summary>Folder holding one file per month (R4).</summary>
    public const string ChaptersFolderName = "chapters";

    /// <summary>Folder holding the immutable copied originals (R1).</summary>
    public const string OriginalsFolderName = "originals";

    /// <summary>Folder holding regenerable derived data. Deleting it is always safe (doc 04 §8).</summary>
    public const string CacheFolderName = "cache";

    /// <summary>Suffix of the in-progress file written by the atomic save protocol (doc 04 §6).</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>Suffix of the rolling backup left beside every saved file (doc 04 §6).</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>Creates a path set rooted at the given project folder.</summary>
    /// <param name="root">The project folder; it need not exist yet.</param>
    public ProjectPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>The absolute project folder path.</summary>
    public string Root { get; }

    /// <summary>Absolute path of <c>book.json</c>.</summary>
    public string BookFile => Path.Combine(Root, BookFileName);

    /// <summary>Absolute path of <c>photos.json</c>.</summary>
    public string PhotosFile => Path.Combine(Root, PhotosFileName);

    /// <summary>Absolute path of <c>journal.json</c>.</summary>
    public string JournalFile => Path.Combine(Root, JournalFileName);

    /// <summary>Absolute path of the <c>chapters/</c> folder.</summary>
    public string ChaptersFolder => Path.Combine(Root, ChaptersFolderName);

    /// <summary>Absolute path of the <c>originals/</c> folder.</summary>
    public string OriginalsFolder => Path.Combine(Root, OriginalsFolderName);

    /// <summary>Absolute path of the <c>cache/</c> folder.</summary>
    public string CacheFolder => Path.Combine(Root, CacheFolderName);

    /// <summary>Absolute path of the single-instance lock file <c>cache/.lock</c> (doc 04 §9).</summary>
    public string LockFile => Path.Combine(CacheFolder, ".lock");

    /// <summary>Absolute path of the cached raw analysis outputs folder.</summary>
    public string AnalysisCacheFolder => Path.Combine(CacheFolder, "analysis");

    /// <summary>Absolute path of the thumbnail cache root.</summary>
    public string ThumbnailCacheFolder => Path.Combine(CacheFolder, "thumbs");

    /// <summary>Absolute path of a thumbnail tier folder, e.g. <c>256</c>, <c>1024</c>, <c>1024a</c> (doc 05).</summary>
    public string ThumbnailTierFolder(string tier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tier);
        return Path.Combine(ThumbnailCacheFolder, tier);
    }

    /// <summary>The chapter file name for a month, <c>{year}-{month:00}.json</c> (doc 04 §2).</summary>
    public static string ChapterFileName(int year, int month)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, 12);
        return string.Create(CultureInfo.InvariantCulture, $"{year:0000}-{month:00}.json");
    }

    /// <summary>Absolute path of a chapter file. Files exist only for months that have pages.</summary>
    public string ChapterFile(int year, int month) => Path.Combine(ChaptersFolder, ChapterFileName(year, month));

    /// <summary>Absolute path of the archived original for a photo, given its project-relative path.</summary>
    public string OriginalFile(string projectRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRelativePath);
        return Path.Combine(Root, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// The content-hash-prefixed file name an imported original is stored under:
    /// <c>{hash16}-{sanitizedOriginalName}</c> (doc 04 §7). The hash prefix guarantees uniqueness
    /// when two imports share a name; the original name is kept for human recognizability.
    /// </summary>
    public static string OriginalFileNameFor(string contentHash, string originalFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        return $"{Ids.HashPrefix(contentHash)}-{SanitizeFileName(originalFileName)}";
    }

    /// <summary>The project-relative <c>originals/…</c> path stored in <see cref="Photo.OriginalPath"/>.</summary>
    public static string OriginalRelativePath(string contentHash, string originalFileName) =>
        $"{OriginalsFolderName}/{OriginalFileNameFor(contentHash, originalFileName)}";

    /// <summary>Replaces characters NTFS rejects, preserving the extension (doc 04 §7).</summary>
    public static string SanitizeFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = fileName.Length <= 256 ? stackalloc char[fileName.Length] : new char[fileName.Length];
        for (var i = 0; i < fileName.Length; i++)
        {
            var c = fileName[i];
            buffer[i] = Array.IndexOf(invalid, c) >= 0 ? '_' : c;
        }

        return new string(buffer).Trim();
    }

    /// <summary>The temp file the atomic save protocol writes before swapping it in (doc 04 §6).</summary>
    public static string TempPathFor(string path) => path + TempSuffix;

    /// <summary>The rolling backup beside a saved file (doc 04 §6).</summary>
    public static string BackupPathFor(string path) => path + BackupSuffix;

    /// <summary>True when the folder looks like a PhotoBook project — that is, it contains <c>book.json</c>.</summary>
    public static bool IsProjectFolder(string folder) =>
        !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, BookFileName));

    /// <summary>
    /// Accepts either a project folder or its <c>book.json</c> ("Open project" = pick the folder or
    /// the file) and returns the path set.
    /// </summary>
    public static ProjectPaths FromFolderOrBookFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var isBookFile = string.Equals(Path.GetFileName(path), BookFileName, StringComparison.OrdinalIgnoreCase);
        var root = isBookFile ? Path.GetDirectoryName(Path.GetFullPath(path)) : path;
        if (string.IsNullOrEmpty(root))
            throw new ArgumentException($"Cannot determine the project folder from '{path}'.", nameof(path));
        return new ProjectPaths(root);
    }

    /// <summary>The chapter files present on disk, ordered by month. Months with no pages have no file.</summary>
    public IEnumerable<string> EnumerateChapterFiles()
    {
        if (!Directory.Exists(ChaptersFolder)) return Enumerable.Empty<string>();
        return Directory.EnumerateFiles(ChaptersFolder, "*.json")
            .Where(f => !IsTransient(f) && !IsSyncConflictCopy(f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Parses <c>{year}-{month:00}.json</c>; returns false when the name does not match.</summary>
    public static bool TryParseChapterFileName(string path, out int year, out int month)
    {
        year = 0;
        month = 0;
        var name = Path.GetFileNameWithoutExtension(path);
        if (name is null || name.Length < 6) return false;
        var dash = name.IndexOf('-');
        if (dash <= 0 || dash == name.Length - 1) return false;
        return int.TryParse(name.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out year)
            && int.TryParse(name.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out month)
            && month is >= 1 and <= 12;
    }

    /// <summary>True for the <c>*.tmp</c> / <c>*.bak</c> siblings the save protocol produces; never project content.</summary>
    public static bool IsTransient(string path) =>
        path.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a OneDrive sync conflict copy such as <c>book-Copy.json</c>; the app ignores these
    /// and says so in a load warning (doc 04 §6).
    /// </summary>
    public static bool IsSyncConflictCopy(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name is not null &&
               (name.EndsWith("-Copy", StringComparison.OrdinalIgnoreCase) ||
                name.Contains(" - Copy", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("-conflict", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Creates the folder skeleton of doc 04 §2, including the cache subfolders. Idempotent.</summary>
    public void EnsureFolders()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ChaptersFolder);
        Directory.CreateDirectory(OriginalsFolder);
        Directory.CreateDirectory(CacheFolder);
        Directory.CreateDirectory(AnalysisCacheFolder);
        Directory.CreateDirectory(ThumbnailTierFolder("256"));
        Directory.CreateDirectory(ThumbnailTierFolder("1024"));
        Directory.CreateDirectory(ThumbnailTierFolder("1024a"));
    }

    /// <inheritdoc/>
    public override string ToString() => Root;
}
