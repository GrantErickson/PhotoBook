namespace PhotoBook.Core.Model;

/// <summary>Where a book's photos are synced from (doc 03 §4, doc 05).</summary>
public enum BookSourceKind
{
    /// <summary>A OneDrive album (bundle) — the "Album + in-app refine" workflow.</summary>
    OneDriveAlbum,

    /// <summary>A OneDrive folder, synced recursively by default.</summary>
    OneDriveFolder,

    /// <summary>A local folder — the offline path (R1).</summary>
    LocalFolder,
}
