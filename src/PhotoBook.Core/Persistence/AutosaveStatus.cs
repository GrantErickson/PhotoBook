using System.Globalization;

namespace PhotoBook.Core.Persistence;

/// <summary>
/// An immutable snapshot of the autosave loop's state, raised on every transition so the shell can
/// show "Saved" / "Saving…" / "Unsaved changes" without polling and without guessing.
/// </summary>
/// <param name="State">What the loop is doing.</param>
/// <param name="LastSavedUtc">When the last successful write completed, or null if there has not been one.</param>
/// <param name="Error">The failure message when <see cref="State"/> is <see cref="AutosaveState.Failed"/>.</param>
public sealed record AutosaveStatus(AutosaveState State, DateTime? LastSavedUtc, string? Error)
{
    /// <summary>The state of a session with no project open.</summary>
    public static AutosaveStatus Closed { get; } = new(AutosaveState.Closed, null, null);

    /// <summary>True while some edit exists only in memory — including while it is being written.</summary>
    public bool HasUnsavedChanges =>
        State is AutosaveState.Dirty or AutosaveState.Saving or AutosaveState.Failed;

    /// <summary>True when the user needs to see something has gone wrong.</summary>
    public bool IsFailed => State == AutosaveState.Failed;

    /// <summary>
    /// A ready-to-show label. Deliberately short — the shell puts it in the status strip beside the
    /// book title, and the timestamp is local because the reader is local.
    /// </summary>
    public string Label => State switch
    {
        AutosaveState.Closed => string.Empty,
        AutosaveState.Saving => "Saving…",
        AutosaveState.Dirty => "Unsaved changes",
        AutosaveState.Failed => "Not saved — " + (Error ?? "the last save failed"),
        _ => LastSavedUtc is { } saved
            ? "Saved " + saved.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)
            : "Saved",
    };
}
