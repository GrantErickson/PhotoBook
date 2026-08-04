using PhotoBook.Core.Model;

namespace PhotoBook.Engine;

/// <summary>
/// Turns a <see cref="PagePlan"/> plus its winning <see cref="TemplateChoice"/> into a concrete
/// <see cref="Page"/>: slot bindings with the phase-6 <see cref="CropState"/>, and the journal
/// assignments that bind a day's atomic text to its slot chain (doc 11).
/// </summary>
public static class PageBuilder
{
    /// <summary>Builds the page and appends anything worth reporting to <paramref name="diagnostics"/>.</summary>
    /// <param name="plan">The planned page.</param>
    /// <param name="choice">The winning template and its kept assignment.</param>
    /// <param name="context">The run environment.</param>
    /// <param name="pageId">A deterministic page id (see <see cref="LayoutRandom.PageId"/>).</param>
    /// <param name="diagnostics">Collector for engine diagnostics.</param>
    public static Page Build(
        PagePlan plan,
        TemplateChoice choice,
        LayoutContext context,
        string pageId,
        List<LayoutDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var page = new Page
        {
            Id = pageId,
            TemplateRef = choice.Library.Id,
            Mirrored = choice.Mirrored,
            Pinned = false,
        };

        var placed = new HashSet<string>(StringComparer.Ordinal);

        if (plan.Kind == PagePlanKind.MultiDay && choice.Oriented.Sections is { } sections)
        {
            for (var i = 0; i < sections.Count && i < choice.SectionAssignments.Count; i++)
            {
                AddPlacements(page, plan, choice.SectionAssignments[i], context, diagnostics, placed);

                var day = plan.Sections[i].Day;
                if (!day.HasJournal) continue;
                var chain = TemplateCatalog.SectionJournalChain(choice.Oriented, sections[i]);
                AddJournal(page, day.Entries, day.Paragraphs, chain, context);
            }
        }
        else
        {
            AddPlacements(page, plan, choice.Assignment, context, diagnostics, placed);

            if (plan.Entries.Count > 0 && plan.JournalChars > 0)
            {
                var chain = TemplateCatalog.JournalChain(choice.Oriented);
                if (chain.Count > 0)
                {
                    AddJournal(page, plan.Entries, plan.Paragraphs, chain, context);
                }
            }
        }

        // Slots the assignment could not fill render amber (R14) — only reachable in the degenerate
        // cases of doc 08 §12, such as an empty Chapter's title page.
        foreach (var slot in choice.Oriented.Slots)
        {
            if (page.PlacementFor(slot.Id) is not null) continue;
            diagnostics.Add(new LayoutDiagnostic
            {
                Kind = LayoutDiagnosticKind.EmptySlot,
                Severity = LayoutSeverity.Warning,
                Message = $"Slot '{slot.Id}' of template '{choice.Library.Id}' has no photo.",
                SlotId = slot.Id,
                Date = plan.PrimaryDate,
            });
        }

        if (!choice.TextFits)
        {
            diagnostics.Add(new LayoutDiagnostic
            {
                Kind = LayoutDiagnosticKind.TextOverflow,
                Severity = LayoutSeverity.Error,
                Message = $"The journal text for {plan.PrimaryDate:yyyy-MM-dd} does not fit any template " +
                          $"of {plan.Photos.Count} photo(s); it will render clipped.",
                Date = plan.PrimaryDate,
            });
        }

        if (plan.Demand > context.Weights.CrowdingDemand)
        {
            diagnostics.Add(new LayoutDiagnostic
            {
                Kind = LayoutDiagnosticKind.Crowding,
                Severity = LayoutSeverity.Info,
                Message = $"Page for {plan.PrimaryDate:yyyy-MM-dd} carries {plan.Demand:0.00} page units of demand.",
                Date = plan.PrimaryDate,
            });
        }

        return page;
    }

    private static void AddPlacements(
        Page page, PagePlan plan, SlotAssignment assignment, LayoutContext context,
        List<LayoutDiagnostic> diagnostics, HashSet<string> placed)
    {
        foreach (var binding in assignment.Bindings)
        {
            if (binding.Photo is null || binding.Slot is null) continue;
            if (!placed.Add(binding.Photo.Id)) continue;

            var crop = SmartCrop.Crop(
                binding.Photo, binding.Slot, plan.Side,
                context.TrimWidthIn, context.TrimHeightIn, context.Weights, plan.SpanWidthFactor);

            page.Placements.Add(new Placement
            {
                SlotId = binding.Slot.Id,
                PhotoId = binding.Photo.Id,
                Crop = crop.Crop,
            });

            if (crop.FocusClipped)
            {
                diagnostics.Add(new LayoutDiagnostic
                {
                    Kind = LayoutDiagnosticKind.FocusClipped,
                    Severity = LayoutSeverity.Info,
                    Message = $"The focus region of '{binding.Photo.Id}' is larger than slot " +
                              $"'{binding.Slot.Id}' can show at zoom 1.0.",
                    PhotoId = binding.Photo.Id,
                    SlotId = binding.Slot.Id,
                    Date = plan.PrimaryDate,
                });
            }

            if (crop.FaceNearGutter)
            {
                diagnostics.Add(new LayoutDiagnostic
                {
                    Kind = LayoutDiagnosticKind.FaceNearGutter,
                    Severity = LayoutSeverity.Warning,
                    Message = $"A face in '{binding.Photo.Id}' could not be cleared of the gutter " +
                              $"caution zone in slot '{binding.Slot.Id}'.",
                    PhotoId = binding.Photo.Id,
                    SlotId = binding.Slot.Id,
                    Date = plan.PrimaryDate,
                });
            }
        }
    }

    /// <summary>
    /// Binds a day's entries to the shortest prefix of the journal chain that holds them; if the whole
    /// chain is needed the text flows through every slot (doc 11's linked slot chain). Text is never
    /// split across days and never shrunk (kernel §9).
    /// </summary>
    private static void AddJournal(
        Page page, IReadOnlyList<JournalEntry> entries, IReadOnlyList<string> paragraphs,
        IReadOnlyList<TextSlot> chain, LayoutContext context)
    {
        if (chain.Count == 0 || entries.Count == 0) return;

        var used = chain.Count;
        for (var length = 1; length <= chain.Count; length++)
        {
            var prefix = new List<TextSlot>(length);
            for (var i = 0; i < length; i++) prefix.Add(chain[i]);
            if (context.FitsText(paragraphs, prefix))
            {
                used = length;
                break;
            }
        }

        var ids = new List<string>(entries.Count);
        foreach (var entry in entries) ids.Add(entry.Id);

        for (var i = 0; i < used; i++)
        {
            page.JournalAssignments.Add(new JournalAssignment
            {
                TextSlotId = chain[i].Id,
                EntryIds = new List<string>(ids),
            });
        }
    }
}
