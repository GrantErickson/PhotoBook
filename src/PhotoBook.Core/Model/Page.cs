namespace PhotoBook.Core.Model;

/// <summary>
/// One printed page of a Chapter (doc 03 §4). A page references a library template <b>xor</b> owns an
/// inline detached snapshot; it never owns both.
/// <para>
/// Page states (kernel §4): <b>Pinned</b> — the user touched it, so "auto-layout rest of chapter"
/// leaves it alone (R16); <b>Detached</b> — the user edited the layout geometry itself, so the page
/// owns a <see cref="DetachedTemplate"/> snapshot and the library template is never mutated (R15).
/// Any manual edit pins; only geometry edits detach.
/// </para>
/// </summary>
public sealed record Page
{
    /// <summary>Opaque stable id assigned at creation, e.g. <c>pg-01H…</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The library <see cref="Template.Id"/> this page uses; null when <see cref="DetachedTemplate"/> is set.</summary>
    public string? TemplateRef { get; set; }

    /// <summary>The inline template snapshot owned by a detached page; null when <see cref="TemplateRef"/> is set (R15).</summary>
    public Template? DetachedTemplate { get; set; }

    /// <summary>True when the template geometry is mirrored for a left-hand page (doc 07).</summary>
    public bool Mirrored { get; set; }

    /// <summary>True when the user has touched this page; auto-layout will not regenerate it (R16).</summary>
    public bool Pinned { get; set; }

    /// <summary>Sparse page-level style override — the most specific level of the cascade (R23).</summary>
    public Style? StyleOverride { get; set; }

    /// <summary>Photo-to-slot bindings with their crops.</summary>
    public IList<Placement> Placements { get; set; } = new List<Placement>();

    /// <summary>Journal text bound to this page's journal slots.</summary>
    public IList<JournalAssignment> JournalAssignments { get; set; } = new List<JournalAssignment>();

    /// <summary>True when the page owns an inline template snapshot (R15). A detached page is always pinned.</summary>
    public bool IsDetached => DetachedTemplate is not null;

    /// <summary>True when exactly one of <see cref="TemplateRef"/> and <see cref="DetachedTemplate"/> is set (doc 03 invariant 9).</summary>
    public bool HasValidTemplateBinding => (TemplateRef is not null) ^ (DetachedTemplate is not null);

    /// <summary>The placement in a given slot, or null when the slot is empty and flagged amber (R14).</summary>
    public Placement? PlacementFor(string slotId) =>
        Placements.FirstOrDefault(p => string.Equals(p.SlotId, slotId, StringComparison.Ordinal));

    /// <summary>
    /// The template this page actually renders with: its detached snapshot, else the library template
    /// resolved by <paramref name="library"/>, mirrored when <see cref="Mirrored"/> is set. Returns
    /// null when the reference cannot be resolved.
    /// </summary>
    /// <param name="library">Resolves a library template id to a template — typically the app's template library.</param>
    public Template? ResolveTemplate(Func<string, Template?> library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var template = DetachedTemplate ?? (TemplateRef is null ? null : library(TemplateRef));
        if (template is null) return null;
        return Mirrored ? template.Mirrored() : template;
    }
}
