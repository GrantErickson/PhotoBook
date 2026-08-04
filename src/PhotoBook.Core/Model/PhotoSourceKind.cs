namespace PhotoBook.Core.Model;

/// <summary>Which kind of source a photo was imported from (doc 03 §3).</summary>
public enum PhotoSourceKind
{
    /// <summary>Microsoft Graph / OneDrive, album or folder.</summary>
    OneDrive,

    /// <summary>A local folder on this machine.</summary>
    Folder,
}
