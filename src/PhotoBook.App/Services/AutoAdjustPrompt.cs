using System.Text;
using System.Windows;
using PhotoBook.Imaging.Auto;

namespace PhotoBook.App.Services;

/// <summary>
/// The confirmation in front of a book-wide auto-adjust run.
///
/// <para>
/// doc 09 §3.8 makes this normative for engine batch commands: the dialog "is explicit, listing
/// concrete page numbers before anything runs", and offers an off-by-default checkbox for the class
/// of thing the command would otherwise refuse to touch. Here that checkbox is hand-edited photos,
/// and it matters for more than symmetry — without it the per-photo <em>Auto</em> button is the only
/// route back for someone who edited a hundred photos and now wants the book consistent again.
/// </para>
///
/// <para>
/// Built from <see cref="MessageBox"/> rather than a custom window because the three-way answer —
/// skip hand edits, include them, or do nothing — maps exactly onto Yes/No/Cancel, and a window whose
/// entire content is one sentence and one choice is not worth a XAML file. The wording carries the
/// checkbox instead of a control: "No" is the opt-in, and the text says so plainly.
/// </para>
/// </summary>
public static class AutoAdjustPrompt
{
    /// <summary>
    /// Asks what to run. Returns null when the user cancelled, otherwise whether hand-edited photos
    /// are included.
    /// </summary>
    /// <param name="plan">What the run would do, already counted.</param>
    public static bool? Ask(AutoAdjustPlan plan)
    {
        var text = Describe(plan);

        // Three buttons, because there are genuinely three answers. Defaulting to the safe one means
        // a stray Enter cannot overwrite a hundred hand edits.
        var buttons = plan.Manual > 0 ? MessageBoxButton.YesNoCancel : MessageBoxButton.OKCancel;

        var answer = MessageBox.Show(
            text, "Auto-adjust photos", buttons, MessageBoxImage.Question, MessageBoxResult.Cancel);

        return answer switch
        {
            MessageBoxResult.OK or MessageBoxResult.Yes => false,
            MessageBoxResult.No => true,
            _ => null,
        };
    }

    /// <summary>
    /// The dialog's text. Separate and public so the wording can be asserted without showing a modal
    /// window — the counts are the whole point of the dialog and getting them wrong is how a user
    /// agrees to something they did not mean.
    /// </summary>
    /// <param name="plan">What the run would do.</param>
    public static string Describe(AutoAdjustPlan plan)
    {
        var text = new StringBuilder();
        var scope = plan.Scope(includeManual: false);

        text.Append(scope == 0
            ? "No photos are waiting to be adjusted."
            : $"{Photos(scope)} will be adjusted from this book's look settings.");

        if (plan.Untouched > 0 && plan.Automatic > 0)
        {
            text.Append(Environment.NewLine)
                .Append(Environment.NewLine)
                .Append($"    {Photos(plan.Untouched)} not adjusted before")
                .Append(Environment.NewLine)
                .Append($"    {Photos(plan.Automatic)} already on auto, re-derived from the current settings");
        }

        if (plan.UpToDate > 0)
        {
            text.Append(Environment.NewLine)
                .Append($"    {Photos(plan.UpToDate)} already up to date and will not change");
        }

        if (plan.Manual > 0)
        {
            text.Append(Environment.NewLine)
                .Append(Environment.NewLine)
                .Append($"{Photos(plan.Manual)} that you edited by hand will be left alone.")
                .Append(Environment.NewLine)
                .Append(Environment.NewLine)
                .Append("Yes — adjust the rest, keep my edits")
                .Append(Environment.NewLine)
                .Append("No — also replace my edits and put those photos back on auto");
        }

        text.Append(Environment.NewLine)
            .Append(Environment.NewLine)
            .Append("Originals are never modified, and the whole run is a single undo.");

        return text.ToString();
    }

    private static string Photos(int count) => count == 1 ? "1 photo" : $"{count} photos";
}
