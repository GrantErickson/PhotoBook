using System.Collections.ObjectModel;

namespace PhotoBook.App.ViewModels;

/// <summary>One row of the keyboard reference: the keys, what they do, and when they apply.</summary>
/// <param name="Keys">The chord as the user presses it, e.g. <c>Ctrl+Z</c> or <c>[</c>.</param>
/// <param name="Action">What happens, in the user's words.</param>
public sealed record ShortcutRow(string Keys, string Action)
{
    /// <summary>
    /// The keys split on <c>+</c> so each one can be drawn as its own key cap — unless the chord
    /// <em>is</em> a plus, in which case splitting on it leaves two blank caps and the row reads as
    /// "Zoom in one step" with nothing to press.
    /// </summary>
    public IReadOnlyList<string> Caps
    {
        get
        {
            var parts = Keys.Split('+', StringSplitOptions.TrimEntries);
            return Array.Exists(parts, string.IsNullOrEmpty) ? [Keys] : parts;
        }
    }
}

/// <summary>A titled block of the reference — one per context in doc 09 §5's table.</summary>
/// <param name="Title">The context, e.g. "Pages tab".</param>
/// <param name="Blurb">One line saying when these keys are live.</param>
/// <param name="Rows">The shortcuts of this context, in the order the doc lists them.</param>
public sealed record ShortcutGroup(string Title, string Blurb, IReadOnlyList<ShortcutRow> Rows);

/// <summary>
/// Doc 09 §5's keyboard map, as data.
///
/// <para>
/// This table is the <b>reference the user reads</b> and it sits beside the router that implements it
/// (<c>BookView.OnShortcutKey</c>) on purpose: a keyboard map nobody can find is not a feature, and a
/// printed map that disagrees with the app is worse than none. Every key listed here is dispatched by
/// that one router or by the window's <c>InputBindings</c>; nothing is listed that the app does not
/// actually do.
/// </para>
/// </summary>
public static class EditorShortcuts
{
    /// <summary>The whole map, grouped by context.</summary>
    public static IReadOnlyList<ShortcutGroup> Groups { get; } = new ReadOnlyCollection<ShortcutGroup>(
    [
        new ShortcutGroup(
            "Anywhere",
            "These work from any panel, in either tab.",
            [
                new ShortcutRow("Ctrl+Z", "Undo the last edit"),
                new ShortcutRow("Ctrl+Y", "Redo  (Ctrl+Shift+Z does the same)"),
                new ShortcutRow("Ctrl+S", "Save now — autosave still runs every 30 seconds"),
                new ShortcutRow("Ctrl+Tab", "Switch between the Photos and Pages tabs"),
                new ShortcutRow("F1", "This list of shortcuts  (? does the same)"),
                new ShortcutRow("Esc", "Back out: close the preview, leave crop or layout mode, close a drawer, clear the selection"),
            ]),

        new ShortcutGroup(
            "Photos tab",
            "With a photo selected in the month grid.",
            [
                new ShortcutRow("Space", "Full-size quick preview of the selected photo"),
                new ShortcutRow("Enter", "Open the inspector for the selection"),
                new ShortcutRow("D", "Change date…"),
                new ShortcutRow("F", "Edit the focus regions the crop keeps in frame"),
                new ShortcutRow("]", "Promote a tier — give the photo more room"),
                new ShortcutRow("[", "Demote a tier — give it less"),
                new ShortcutRow("E", "Exclude from the book (the original file is kept)"),
            ]),

        new ShortcutGroup(
            "Pages tab",
            "While the page canvas is showing.",
            [
                new ShortcutRow("Page Down", "Next page"),
                new ShortcutRow("Page Up", "Previous page"),
                new ShortcutRow("Home", "First page of the month"),
                new ShortcutRow("End", "Last page of the month"),
                new ShortcutRow("S", "Single page ↔ spread"),
                new ShortcutRow("T", "Template gallery for this page"),
                new ShortcutRow("L", "Edit this page's layout"),
                new ShortcutRow("B", "Show or hide the bins"),
                new ShortcutRow("G", "Bleed, trim, safe and gutter guides"),
                new ShortcutRow("F11", "Fit the page to the window"),
                new ShortcutRow("Ctrl+=", "Zoom the viewport in  (Ctrl+wheel does the same)"),
                new ShortcutRow("Ctrl+-", "Zoom the viewport out"),
            ]),

        new ShortcutGroup(
            "A slot on the page",
            "After clicking a photo slot, or an empty amber one.",
            [
                new ShortcutRow("Tab", "Select the next slot  (Shift+Tab for the previous)"),
                new ShortcutRow("Enter", "Edit the crop"),
                new ShortcutRow("Del", "Unplace the photo — it goes to the Unplaced bin"),
            ]),

        new ShortcutGroup(
            "Crop mode",
            "Inside a slot, after Enter or a double-click.",
            [
                new ShortcutRow("Drag", "Pan the photo inside the slot"),
                new ShortcutRow("Wheel", "Zoom about the pointer"),
                new ShortcutRow("+", "Zoom in one step"),
                new ShortcutRow("-", "Zoom out one step"),
                new ShortcutRow("Arrows", "Nudge the crop  (hold Shift for a bigger step)"),
                new ShortcutRow("0", "Back to the automatic smart crop"),
                new ShortcutRow("Esc", "Leave crop mode"),
            ]),

        new ShortcutGroup(
            "Bins",
            "With a photo selected in the Unplaced, Upcoming or Outside-the-year bin.",
            [
                new ShortcutRow("E", "Remove it from the book — the original file stays on disk"),
                new ShortcutRow("Del", "The same thing"),
            ]),

        new ShortcutGroup(
            "Layout edit mode",
            "While L has the slot handles showing.",
            [
                new ShortcutRow("Tab", "Select the next container"),
                new ShortcutRow("Del", "Delete the selected container"),
                new ShortcutRow("Esc", "Leave layout edit mode"),
            ]),

        new ShortcutGroup(
            "Drag and drop",
            "Held while dropping a photo.",
            [
                new ShortcutRow("Ctrl", "Replace instead of swap — the displaced photo goes to the Unplaced bin"),
            ]),
    ]);
}

/// <summary>The model behind the shortcuts window; nothing but the map.</summary>
public sealed class ShortcutsViewModel
{
    /// <summary>Doc 09 §5, grouped by context.</summary>
    public IReadOnlyList<ShortcutGroup> Groups => EditorShortcuts.Groups;

    /// <summary>
    /// The same groups dealt into two balanced columns.
    /// <para>
    /// A wrapping panel would align every card to the tallest one in its row and leave a hole under
    /// the short ones; columns of independently stacked cards read as one continuous table, which is
    /// what a reference is. Balance is by row count, so neither column runs off the bottom alone.
    /// </para>
    /// </summary>
    public IReadOnlyList<IReadOnlyList<ShortcutGroup>> Columns { get; } = Deal(EditorShortcuts.Groups, 2);

    private static IReadOnlyList<IReadOnlyList<ShortcutGroup>> Deal(IReadOnlyList<ShortcutGroup> groups, int count)
    {
        var columns = new List<ShortcutGroup>[count];
        var weights = new int[count];
        for (var i = 0; i < count; i++)
        {
            columns[i] = [];
        }

        foreach (var group in groups)
        {
            var lightest = Array.IndexOf(weights, weights.Min());
            columns[lightest].Add(group);

            // A card costs its rows plus the header and blurb above them.
            weights[lightest] += group.Rows.Count + 3;
        }

        return [.. columns.Select(IReadOnlyList<ShortcutGroup> (c) => c)];
    }
}
