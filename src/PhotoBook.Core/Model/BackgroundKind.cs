namespace PhotoBook.Core.Model;

/// <summary>The kind of page background (doc 10 §6). v1 ships <see cref="Solid"/> only.</summary>
public enum BackgroundKind
{
    /// <summary>A flat color — <c>#000000</c> in v1 (R21).</summary>
    Solid,

    /// <summary>Reserved for the scrapbook-style image backgrounds of a later version (R21).</summary>
    Image,
}
