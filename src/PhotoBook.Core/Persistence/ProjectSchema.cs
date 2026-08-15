using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhotoBook.Core.Persistence;

/// <summary>
/// Current <c>schemaVersion</c> values and the forward-migration policy of doc 04 §5.
/// <para>
/// Additive changes (a new optional field with a default) do <b>not</b> bump a version — the
/// <c>[JsonExtensionData]</c> bag on every root record makes them round-trip safe in both
/// directions. Shape changes bump the file's version and ship a pure migration step registered here.
/// Migration happens in memory on load; opening a project never mutates it.
/// </para>
/// </summary>
public static class ProjectSchema
{
    /// <summary>Current schema version of <c>book.json</c>.</summary>
    public const int BookVersion = 1;

    /// <summary>Current schema version of <c>photos.json</c>.</summary>
    public const int PhotosVersion = 1;

    /// <summary>Current schema version of <c>journal.json</c>.</summary>
    public const int JournalVersion = 1;

    /// <summary>Current schema version of a chapter file.</summary>
    public const int ChapterVersion = 1;

    /// <summary>Current schema version of a template document (doc 07).</summary>
    public const int TemplateVersion = 1;

    /// <summary>Current schema version of a print profile document (doc 12).</summary>
    public const int PrintProfileVersion = 1;

    /// <summary>Current schema version of a cached analysis document (doc 06).</summary>
    public const int AnalysisCacheVersion = 1;

    /// <summary>
    /// The registered <c>vN → vN+1</c> steps per file kind. Each step is a pure, in-memory
    /// transformation with its own golden-file test, and chains are kept forever: a v1 file written
    /// in 2026 must still open in the 2036 build. Empty while every file is at version 1.
    /// </summary>
    private static readonly Dictionary<(ProjectFileKind Kind, int FromVersion), Func<JsonObject, JsonObject>> Migrations = new();

    /// <summary>The current version of a file kind.</summary>
    public static int CurrentVersion(ProjectFileKind kind) => kind switch
    {
        ProjectFileKind.Book => BookVersion,
        ProjectFileKind.Photos => PhotosVersion,
        ProjectFileKind.Journal => JournalVersion,
        ProjectFileKind.Chapter => ChapterVersion,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown project file kind."),
    };

    /// <summary>The canonical file name of a file kind, used in diagnostics.</summary>
    public static string FileNameOf(ProjectFileKind kind) => kind switch
    {
        ProjectFileKind.Book => ProjectPaths.BookFileName,
        ProjectFileKind.Photos => ProjectPaths.PhotosFileName,
        ProjectFileKind.Journal => ProjectPaths.JournalFileName,
        ProjectFileKind.Chapter => "chapters/*.json",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown project file kind."),
    };

    /// <summary>
    /// Migrates a parsed document forward to the current version, in memory (doc 04 §5). A document
    /// written by a newer app is never guessed forward: it raises
    /// <see cref="ProjectSchemaTooNewException"/> so the caller can open the project read-only.
    /// </summary>
    /// <param name="root">The parsed root object.</param>
    /// <param name="kind">Which file this is.</param>
    /// <param name="fileName">Path or name used in diagnostics.</param>
    /// <returns>The document at the current schema version.</returns>
    public static JsonObject Migrate(JsonObject root, ProjectFileKind kind, string fileName)
    {
        ArgumentNullException.ThrowIfNull(root);
        var current = CurrentVersion(kind);
        var version = ReadVersion(root);

        if (version > current)
            throw new ProjectSchemaTooNewException(fileName, version, current);

        while (version < current)
        {
            if (!Migrations.TryGetValue((kind, version), out var step))
                throw new ProjectFormatException(fileName, $"no migration is registered from schemaVersion {version} to {version + 1}.");
            root = step(root);
            version++;
            root["schemaVersion"] = version;
        }

        return root;
    }

    /// <summary>Reads the root <c>schemaVersion</c>; a document without one is treated as version 1.</summary>
    public static int ReadVersion(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.TryGetPropertyValue("schemaVersion", out var node) && node is not null &&
            node.GetValueKind() == JsonValueKind.Number)
        {
            return node.GetValue<int>();
        }

        return 1;
    }
}
