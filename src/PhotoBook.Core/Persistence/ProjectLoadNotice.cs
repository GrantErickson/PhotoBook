namespace PhotoBook.Core.Persistence;

/// <summary>Something worth telling the user about a load — recovery, a swept temp file, an ignored conflict copy (doc 04 §6).</summary>
/// <param name="Kind">What happened.</param>
/// <param name="File">The file it happened to.</param>
/// <param name="Message">A ready-to-show sentence.</param>
public sealed record ProjectLoadNotice(ProjectLoadNoticeKind Kind, string File, string Message);
