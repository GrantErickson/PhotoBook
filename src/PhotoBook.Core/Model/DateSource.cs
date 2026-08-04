namespace PhotoBook.Core.Model;

/// <summary>How a photo's effective date was established — the date chain of kernel §10.</summary>
public enum DateSource
{
    /// <summary>EXIF <c>DateTimeOriginal</c>. Trusted.</summary>
    Exif,

    /// <summary>Graph <c>photo.takenDateTime</c> for OneDrive items. Trusted.</summary>
    Graph,

    /// <summary>File last-modified time — the last resort; sets <see cref="Photo.DateUncertain"/>.</summary>
    FileMtime,

    /// <summary>The user re-dated the photo in the Photos tab (R6). Permanent user intent.</summary>
    User,
}
