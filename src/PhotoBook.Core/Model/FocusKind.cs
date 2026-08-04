namespace PhotoBook.Core.Model;

/// <summary>
/// Where a focus region came from. Fusion priority is <c>user &gt; person &gt; face &gt; saliency</c>
/// (kernel §4, doc 06): human intent beats a name, a name beats an anonymous face, any face beats
/// "something contrasty".
/// </summary>
public enum FocusKind
{
    /// <summary>Drawn or corrected by the user in the Photos tab (R25). Outranks everything.</summary>
    User,

    /// <summary>A named person, from a OneDrive people tag carrying a rect (doc 05).</summary>
    Person,

    /// <summary>An anonymous face box from the local detector (YuNet, doc 06).</summary>
    Face,

    /// <summary>A salient blob from the saliency model (U2-Netp, doc 06) — the no-faces safety net.</summary>
    Saliency,
}
