namespace PhotoBook.Core.Model;

/// <summary>Horizontal text alignment inside a <see cref="TextSlot"/>. Never flipped by mirroring — reading direction beats symmetry (doc 07).</summary>
public enum TextAlign
{
    /// <summary>Left aligned — the default for journal and caption text.</summary>
    Left,

    /// <summary>Centered — the default for month titles.</summary>
    Center,

    /// <summary>Right aligned.</summary>
    Right,
}
