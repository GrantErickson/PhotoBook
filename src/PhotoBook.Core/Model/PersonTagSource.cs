namespace PhotoBook.Core.Model;

/// <summary>Where a <see cref="PersonTag"/> came from.</summary>
public enum PersonTagSource
{
    /// <summary>Pulled from consumer OneDrive's people tags during ingestion (doc 05).</summary>
    OneDrive,

    /// <summary>Entered by the user.</summary>
    User,
}
