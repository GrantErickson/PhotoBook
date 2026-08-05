using System.Text;

namespace PhotoBook.Core.Persistence;

/// <summary>
/// What opening a project had to do to get it open (doc 04 §6), turned into something worth showing
/// a human. <see cref="ProjectStore"/> already recovers silently — it deletes the stray <c>*.tmp</c>
/// of an interrupted write and falls back to a file's rolling <c>.bak</c> when the primary will not
/// parse — but a recovery nobody is told about is indistinguishable from data loss. This groups the
/// raw <see cref="ProjectLoadNotice"/> list by what actually happened and says it plainly.
/// </summary>
public sealed class ProjectRecoveryReport
{
    /// <summary>The report of a clean open: nothing happened, nothing to say.</summary>
    public static ProjectRecoveryReport Clean { get; } = new(Array.Empty<ProjectLoadNotice>());

    /// <summary>Groups a loader's notices.</summary>
    /// <param name="notices">The notices from <see cref="ProjectLoadResult{T}.Notices"/>.</param>
    public ProjectRecoveryReport(IReadOnlyList<ProjectLoadNotice> notices)
    {
        ArgumentNullException.ThrowIfNull(notices);
        Notices = notices;
        RecoveredFromBackup = Of(ProjectLoadNoticeKind.RecoveredFromBackup);
        DiscardedTempFiles = Of(ProjectLoadNoticeKind.DeletedStrayTemp);
        IgnoredConflictCopies = Of(ProjectLoadNoticeKind.IgnoredConflictCopy);
        MissingFilesDefaulted = Of(ProjectLoadNoticeKind.MissingFileDefaulted);

        IReadOnlyList<ProjectLoadNotice> Of(ProjectLoadNoticeKind kind) =>
            notices.Where(n => n.Kind == kind).ToList();
    }

    /// <summary>Every notice, in load order.</summary>
    public IReadOnlyList<ProjectLoadNotice> Notices { get; }

    /// <summary>Files whose primary copy was unreadable and whose <c>.bak</c> was used instead.</summary>
    public IReadOnlyList<ProjectLoadNotice> RecoveredFromBackup { get; }

    /// <summary>Leftover <c>*.tmp</c> files — the fingerprint of a write interrupted by a crash.</summary>
    public IReadOnlyList<ProjectLoadNotice> DiscardedTempFiles { get; }

    /// <summary>Sync-engine conflict copies (<c>book-Copy.json</c>) that were ignored.</summary>
    public IReadOnlyList<ProjectLoadNotice> IgnoredConflictCopies { get; }

    /// <summary>Optional files that were absent and started empty.</summary>
    public IReadOnlyList<ProjectLoadNotice> MissingFilesDefaulted { get; }

    /// <summary>True when the open was uneventful.</summary>
    public bool IsClean => Notices.Count == 0;

    /// <summary>True when a file's content came from its backup — the only case that lost anything.</summary>
    public bool RecoveredAnything => RecoveredFromBackup.Count > 0;

    /// <summary>
    /// True when the previous session ended mid-write: either a file had to come from its backup, or
    /// a temp file was left behind. Both mean the app did not shut down cleanly.
    /// </summary>
    public bool InterruptedWriteDetected => RecoveredAnything || DiscardedTempFiles.Count > 0;

    /// <summary>True when there is anything the user should be told about.</summary>
    public bool NeedsAttention =>
        RecoveredAnything || DiscardedTempFiles.Count > 0 || IgnoredConflictCopies.Count > 0 ||
        MissingFilesDefaulted.Count > 0;

    /// <summary>One line naming what happened, suitable for a banner or a dialog title.</summary>
    public string Headline
    {
        get
        {
            if (RecoveredAnything)
            {
                return DescribeFiles(RecoveredFromBackup) + " could not be read and " +
                       (RecoveredFromBackup.Count == 1 ? "was" : "were") + " restored from the last good save.";
            }

            if (DiscardedTempFiles.Count > 0)
            {
                return "This project was not closed cleanly; an unfinished save was discarded.";
            }

            if (IgnoredConflictCopies.Count > 0)
            {
                return DescribeFiles(IgnoredConflictCopies) + " looks like a sync conflict copy and was ignored.";
            }

            if (MissingFilesDefaulted.Count > 0)
            {
                return DescribeFiles(MissingFilesDefaulted) + " was missing and started empty.";
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// The full explanation: what happened to each file, plus what the app did about it. Ends with
    /// the reassurance that matters — a recovered file is rewritten by the next save, so the project
    /// heals itself rather than staying one crash away from the same problem.
    /// </summary>
    public string Detail
    {
        get
        {
            if (IsClean)
            {
                return string.Empty;
            }

            var text = new StringBuilder();
            foreach (var notice in Notices)
            {
                text.AppendLine(notice.Message);
            }

            if (RecoveredAnything)
            {
                text.AppendLine();
                text.AppendLine(
                    "Anything changed after that save is gone, but the project is intact and will be " +
                    "rewritten in full by the next save.");
            }

            return text.ToString().TrimEnd();
        }
    }

    /// <summary>Headline and detail as one block of text.</summary>
    public override string ToString() =>
        IsClean ? string.Empty : (Headline + Environment.NewLine + Environment.NewLine + Detail).Trim();

    private static string DescribeFiles(IReadOnlyList<ProjectLoadNotice> notices) => notices.Count switch
    {
        0 => "A project file",
        1 => "‘" + notices[0].File + "’",
        2 => "‘" + notices[0].File + "’ and ‘" + notices[1].File + "’",
        _ => "‘" + notices[0].File + "’ and " + (notices.Count - 1) + " other files",
    };
}
