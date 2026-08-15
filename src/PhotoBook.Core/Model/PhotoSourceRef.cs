namespace PhotoBook.Core.Model;

/// <summary>Where a photo came from, and when it was imported (doc 03 §3).</summary>
public sealed record PhotoSourceRef
{
    /// <summary>The kind of source system.</summary>
    public PhotoSourceKind Kind { get; set; } = PhotoSourceKind.Folder;

    /// <summary>Graph <c>driveItem</c> id; present for OneDrive sources and used to correlate re-syncs.</summary>
    public string? DriveItemId { get; set; }

    /// <summary>When the bytes were copied into the project, UTC.</summary>
    public DateTime ImportedAtUtc { get; set; }
}
