using System.Globalization;
using PhotoBook.Core.Model;

namespace PhotoBook.Engine;

/// <summary>
/// The auto-layout engine of doc 08 — the core of PhotoBook and the thing the north star is about:
/// <i>"The automatic layout being awesome is the most important part. Edits should really be
/// tweaks."</i> (R27).
/// <para>
/// <c>LayoutChapter</c> is a pure, deterministic function of its <see cref="LayoutRequest"/>: no
/// I/O, no wall clock, no ambient state, no unseeded randomness. The same request produces a
/// field-for-field identical <see cref="LayoutResult"/> on any machine, which is what makes golden
/// layout tests possible (doc 13).
/// </para>
/// <para>The six phases, in order (kernel §7):</para>
/// <list type="number">
/// <item><description><see cref="DayGrouping"/> — photos and journal entries onto one date axis.</description></item>
/// <item><description><see cref="DemandModel"/> — how much page area each day deserves.</description></item>
/// <item><description><see cref="PagePartitioner"/> — exact DP over the day sequence.</description></item>
/// <item><description><see cref="TemplateSelector"/> — hard filters, then the weighted soft score.</description></item>
/// <item><description><see cref="AssignmentCost"/> / <see cref="Hungarian"/> — exact photo → slot matching.</description></item>
/// <item><description><see cref="SmartCrop"/> — Focus Regions to <see cref="CropState"/>.</description></item>
/// </list>
/// </summary>
public static class LayoutEngine
{
    /// <summary>
    /// Lays out one Chapter (doc 08 §1). Honors Pinned and Detached pages as immovable anchors, and
    /// with <see cref="LayoutRequest.DryRun"/> stops after phase 3 to compute
    /// <see cref="LayoutResult.AffectedPages"/> for the R16 warning without producing pages.
    /// </summary>
    public static LayoutResult LayoutChapter(LayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Chapter);

        var run = new Run(request);
        return run.Execute();
    }

    /// <summary>
    /// The cheap pass behind the R16 warning modal: phases 1–3 only, returning which pages would be
    /// replaced and how many pages the run would produce. Equivalent to
    /// <c>LayoutChapter(request with { DryRun = true })</c>.
    /// </summary>
    public static LayoutResult DryRun(LayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return LayoutChapter(request with { DryRun = true });
    }

    /// <summary>One execution's mutable working state; never escapes the call.</summary>
    private sealed class Run
    {
        private readonly LayoutRequest _request;
        private readonly ChapterInput _chapter;
        private readonly LayoutWeights _weights;
        private readonly TemplateCatalog _catalog;
        private readonly LayoutContext _context;
        private readonly List<LayoutDiagnostic> _diagnostics = [];
        private readonly Dictionary<string, Photo> _photosById = new(StringComparer.Ordinal);
        private readonly PacingMemory _memory = new();

        public Run(LayoutRequest request)
        {
            _request = request;
            _chapter = request.Chapter;
            _weights = request.Weights ?? LayoutWeights.Default;
            _catalog = new TemplateCatalog(request.Templates, _chapter.PageSize);
            _context = new LayoutContext(
                _catalog, request.Style, _chapter.TrimWidthIn, _chapter.TrimHeightIn,
                request.TextMeasurer ?? DefaultTextMeasurer.Instance, request.Seed, _weights);
        }

        /// <summary>
        /// Which side of a Spread the page at a 0-based Chapter page index falls on. Pages pair
        /// <c>2k</c> / <c>2k+1</c> (doc 03 §9), offset by
        /// <see cref="LayoutRequest.StartParity"/> so a Chapter that does not open on a left-hand page
        /// still mirrors and gutter-nudges correctly.
        /// </summary>
        private PageSide SideOf(int pageIndex) =>
            Spreads.SideOf(pageIndex + (_request.StartParity == PageSide.Right ? 1 : 0));

        public LayoutResult Execute()
        {
            var photos = ChapterPhotos();
            foreach (var photo in photos) _photosById[photo.Id] = photo;

            var existing = _chapter.ExistingPages;
            var keep = KeepMask(existing, photos);

            var affected = new List<int>();
            var pinnedKept = new List<int>();
            for (var i = 0; i < existing.Count; i++)
            {
                if (keep[i])
                {
                    if (existing[i].Pinned || existing[i].IsDetached) pinnedKept.Add(i + 1);
                }
                else
                {
                    affected.Add(i + 1);
                }
            }

            var anchoredPhotoIds = AnchoredPhotoIds(existing, keep);
            var assignedEntryIds = AssignedEntryIds(existing, keep);
            var pool = PhotoPool(photos, anchoredPhotoIds);
            var entries = EntryPool(assignedEntryIds, pool);

            var days = DayGrouping.GroupDays(pool, entries);

            // The month title page (R24) is produced directly by phase 4 and does not participate in
            // partitioning; its hero photos are reserved out of the day pool so a photo still appears
            // in exactly one placement. Reserving first and folding second keeps a day that loses its
            // only photo to the title page from losing its journal text with it.
            var titlePlan = BuildTitlePlan(ref days, existing, keep);
            days = FoldJournalOnlyDays(days);

            var layout = BuildRunLayout(existing, keep, days, titlePlan);

            if (affected.Count == 0 && layout.TotalPlannedPages == 0 && _chapter.ExistingPages.Count > 0)
            {
                _diagnostics.Add(new LayoutDiagnostic
                {
                    Kind = LayoutDiagnosticKind.NothingToDo,
                    Severity = LayoutSeverity.Info,
                    Message = "Every page in scope is pinned; nothing was regenerated.",
                });
            }

            if (_request.DryRun)
            {
                // Phases 1–3 already know which photos land on a page, so the R16 modal can report the
                // Unplaced bin without any template scoring at all.
                var planned = new HashSet<string>(anchoredPhotoIds, StringComparer.Ordinal);
                foreach (var item in layout.Items)
                {
                    if (item is not RunSlot run) continue;
                    foreach (var plan in run.Plans)
                    {
                        foreach (var photo in plan.Photos) planned.Add(photo.Id);
                    }
                }

                return new LayoutResult
                {
                    Pages = existing,
                    AffectedPages = affected,
                    InsertedAfterPages = layout.InsertedAfterPages,
                    PinnedPagesKept = pinnedKept,
                    UnplacedPhotoIds = OrderedUnplaced(photos, planned),
                    Diagnostics = _diagnostics,
                    GeneratedPageCount = layout.TotalPlannedPages,
                    IsDryRun = true,
                };
            }

            var pages = Emit(layout, out var generated);

            var placed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var page in pages)
            {
                foreach (var placement in page.Placements) placed.Add(placement.PhotoId);
            }

            return new LayoutResult
            {
                Pages = pages,
                AffectedPages = affected,
                InsertedAfterPages = layout.InsertedAfterPages,
                PinnedPagesKept = pinnedKept,
                UnplacedPhotoIds = OrderedUnplaced(photos, placed),
                Diagnostics = _diagnostics,
                GeneratedPageCount = generated,
                IsDryRun = false,
            };
        }

        // ── Inputs ────────────────────────────────────────────────────────────────────────────

        private IReadOnlyList<Photo> ChapterPhotos()
        {
            var members = _chapter.Photos.Where(p => p.BelongsToChapter(_chapter.Year, _chapter.Month));
            return DayGrouping.SortPhotos(members);
        }

        private bool[] KeepMask(IReadOnlyList<Page> pages, IReadOnlyList<Photo> photos)
        {
            var keep = new bool[pages.Count];
            for (var i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                var anchored = page.IsDetached || (page.Pinned && !_request.IncludePinnedPages);

                keep[i] = _request.Scope switch
                {
                    LayoutScope.InsertUnplaced => true,
                    LayoutScope.RestOfChapter rest => anchored || i + 1 < Math.Max(1, rest.FromPageNumber),
                    // "Lay out this day" touches that Day Group's pages only; the month title page
                    // belongs to the Chapter, not to a day, so it is never one of them (R24).
                    LayoutScope.SingleDay single => anchored || IsTitlePage(page) || !PageCovers(page, single.Date),
                    _ => anchored,
                };
            }

            return keep;
        }

        private bool IsTitlePage(Page page) =>
            (page.DetachedTemplate ?? _catalog.Find(page.TemplateRef))?.Kind == TemplateKind.MonthTitle;

        private bool PageCovers(Page page, DateOnly date)
        {
            foreach (var placement in page.Placements)
            {
                if (_photosById.TryGetValue(placement.PhotoId, out var photo) && photo.TakenOn == date) return true;
            }

            foreach (var assignment in page.JournalAssignments)
            {
                foreach (var entryId in assignment.EntryIds)
                {
                    var entry = _chapter.JournalEntries.FirstOrDefault(e => string.Equals(e.Id, entryId, StringComparison.Ordinal));
                    if (entry is not null && entry.EffectiveDate == date) return true;
                }
            }

            return false;
        }

        private static HashSet<string> AnchoredPhotoIds(IReadOnlyList<Page> pages, bool[] keep)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < pages.Count; i++)
            {
                if (!keep[i]) continue;
                foreach (var placement in pages[i].Placements) ids.Add(placement.PhotoId);
            }

            return ids;
        }

        private static HashSet<string> AssignedEntryIds(IReadOnlyList<Page> pages, bool[] keep)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < pages.Count; i++)
            {
                if (!keep[i]) continue;
                foreach (var assignment in pages[i].JournalAssignments)
                {
                    foreach (var entryId in assignment.EntryIds) ids.Add(entryId);
                }
            }

            return ids;
        }

        private IReadOnlyList<Photo> PhotoPool(IReadOnlyList<Photo> photos, HashSet<string> anchored)
        {
            var unplaced = _chapter.UnplacedPhotoIds;
            var pool = new List<Photo>(photos.Count);

            foreach (var photo in photos)
            {
                if (anchored.Contains(photo.Id)) continue;

                switch (_request.Scope)
                {
                    case LayoutScope.InsertUnplaced:
                        if (!unplaced.Contains(photo.Id)) continue;
                        break;
                    case LayoutScope.SingleDay single:
                        if (photo.TakenOn != single.Date || unplaced.Contains(photo.Id)) continue;
                        break;
                    default:
                        if (unplaced.Contains(photo.Id)) continue;
                        break;
                }

                pool.Add(photo);
            }

            return pool;
        }

        private IReadOnlyList<JournalEntry> EntryPool(HashSet<string> assigned, IReadOnlyList<Photo> pool)
        {
            var poolDates = new HashSet<DateOnly>();
            foreach (var photo in pool) poolDates.Add(photo.TakenOn);

            var entries = new List<JournalEntry>();
            foreach (var entry in _chapter.JournalEntries)
            {
                if (entry.Excluded) continue;
                if (entry.EffectiveDate.Year != _chapter.Year || entry.EffectiveDate.Month != _chapter.Month) continue;
                if (assigned.Contains(entry.Id)) continue;

                switch (_request.Scope)
                {
                    case LayoutScope.SingleDay single when entry.EffectiveDate != single.Date:
                        continue;
                    case LayoutScope.InsertUnplaced when !poolDates.Contains(entry.EffectiveDate):
                        continue;
                }

                entries.Add(entry);
            }

            return entries;
        }

        /// <summary>
        /// Doc 08 §12's journal-only day. When the library offers a multiDay section with zero image
        /// slots the day survives to partitioning untouched; the v1 library does not, so the day's
        /// text rides along on the nearest day that has photos — its text stays whole and in date
        /// order, which is what R5 and kernel §9 actually require.
        /// </summary>
        private IReadOnlyList<LayoutDay> FoldJournalOnlyDays(IReadOnlyList<LayoutDay> days)
        {
            if (_catalog.SupportsTextOnlySections) return days;
            if (!days.Any(d => d.IsJournalOnly)) return days;

            var withPhotos = days.Where(d => d.Photos.Count > 0).ToList();
            if (withPhotos.Count == 0)
            {
                foreach (var day in days.Where(d => d.IsJournalOnly))
                {
                    _diagnostics.Add(new LayoutDiagnostic
                    {
                        Kind = LayoutDiagnosticKind.NoTemplate,
                        Severity = LayoutSeverity.Warning,
                        Message = $"The journal entry for {day.Date:yyyy-MM-dd} has no photos to share a page with.",
                        Date = day.Date,
                    });
                }

                return [];
            }

            var carried = new Dictionary<DateOnly, List<JournalEntry>>();
            foreach (var day in days)
            {
                if (!day.IsJournalOnly) continue;

                // Prefer the next photo-bearing day, else the previous one — closest in date wins.
                var host = withPhotos.FirstOrDefault(d => d.Date > day.Date) ?? withPhotos[^1];
                if (!carried.TryGetValue(host.Date, out var list))
                {
                    list = [];
                    carried[host.Date] = list;
                }

                list.AddRange(day.Entries);

                _diagnostics.Add(new LayoutDiagnostic
                {
                    Kind = LayoutDiagnosticKind.JournalOnlyDayCarried,
                    Severity = LayoutSeverity.Info,
                    Message = $"The journal-only day {day.Date:yyyy-MM-dd} shares the page of {host.Date:yyyy-MM-dd}; " +
                              "no multiDay template in the library offers a photo-less section.",
                    Date = day.Date,
                });
            }

            var result = new List<LayoutDay>(withPhotos.Count);
            foreach (var day in withPhotos)
            {
                if (!carried.TryGetValue(day.Date, out var extra))
                {
                    result.Add(day);
                    continue;
                }

                var merged = new List<JournalEntry>(day.Entries);
                merged.AddRange(extra);
                merged = merged
                    .OrderBy(e => e.EffectiveDate)
                    .ThenBy(e => e.Occurrence)
                    .ThenBy(e => e.Id, StringComparer.Ordinal)
                    .ToList();
                result.Add(day with { Entries = merged });
            }

            return result;
        }

        // ── Month title page (R24) ────────────────────────────────────────────────────────────

        private PagePlan? BuildTitlePlan(ref IReadOnlyList<LayoutDay> days, IReadOnlyList<Page> existing, bool[] keep)
        {
            if (!_request.GenerateMonthTitlePage) return null;

            var wants = _request.Scope switch
            {
                LayoutScope.Whole => true,
                LayoutScope.RestOfChapter rest => rest.FromPageNumber <= 1,
                _ => false,
            };
            if (!wants) return null;

            // Never a second title page: a kept page that already is one wins.
            for (var i = 0; i < existing.Count; i++)
            {
                if (!keep[i]) continue;
                var template = existing[i].DetachedTemplate ?? _catalog.Find(existing[i].TemplateRef);
                if (template?.Kind == TemplateKind.MonthTitle) return null;
            }

            if (_catalog.MonthTitle.Count == 0) return null;

            var available = days.Sum(d => d.Photos.Count);

            if (available == 0)
            {
                // Empty Chapter (doc 08 §12): the month title page alone, its slots flagged amber
                // (R14). Not an error — the dashboard simply shows the Chapter as empty.
                var minimal = _catalog.MonthTitle.OrderBy(t => t.PhotoCount)
                    .ThenBy(t => t.Id, StringComparer.Ordinal).First();
                return new PagePlan
                {
                    Kind = PagePlanKind.MonthTitle,
                    Photos = [],
                    Side = PageSide.Left,
                    PrimaryDate = new DateOnly(_chapter.Year, _chapter.Month, 1),
                    StableKey = $"{_chapter.Year:D4}-{_chapter.Month:D2}|title|empty",
                    ForcedTemplateId = minimal.Id,
                    Demand = 0,
                };
            }

            // The title page borrows photos from the month, so it may never eat a meaningful share of
            // a thin Chapter — at most one photo in four — and never the last photo of a Chapter that
            // has journal text to place, which would strand the text.
            var budget = Math.Max(1, available / 4);
            var ceiling = days.Any(d => d.HasJournal) ? Math.Max(0, available - 1) : available;
            var wanted = _catalog.MonthTitle
                .Select(t => t.PhotoCount)
                .Where(c => c <= available && c <= budget && c <= ceiling)
                .DefaultIfEmpty(0)
                .Max();

            var heroes = HeroPhotos(days);
            var take = Math.Min(wanted, heroes.Count);
            if (take <= 0) return null;

            var chosen = heroes.Take(take).ToList();
            var reserved = new HashSet<string>(chosen.Select(p => p.Id), StringComparer.Ordinal);
            days = RemovePhotos(days, reserved);

            return new PagePlan
            {
                Kind = PagePlanKind.MonthTitle,
                Photos = chosen,
                Side = PageSide.Left,
                PrimaryDate = new DateOnly(_chapter.Year, _chapter.Month, 1),
                StableKey = $"{_chapter.Year:D4}-{_chapter.Month:D2}|title|" + string.Join('+', chosen.Select(p => p.Id)),
                Demand = 1.0,
            };
        }

        /// <summary>
        /// Hero ranking for the title page (doc 08 §2): the highest-scoring S-tier photo of the
        /// month, tie-broken by the seeded hash. Tier leads, but a photo that would be butchered by
        /// the title slot's aspect (a panorama in a full-bleed hero) is ranked down — the title page
        /// is the one page a bad crop is most visible on. Taking from a photo-rich day is preferred
        /// so reserving a hero perturbs the day partitioning as little as possible.
        /// </summary>
        private List<Photo> HeroPhotos(IReadOnlyList<LayoutDay> days)
        {
            var all = new List<Photo>();
            var dayWeight = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var day in days)
            {
                foreach (var photo in day.Photos)
                {
                    all.Add(photo);
                    dayWeight[photo.Id] = day.Photos.Count;
                }
            }

            var slotAspects = _catalog.MonthTitle
                .SelectMany(t => t.Slots)
                .Select(s => s.Aspect > 0 ? s.Aspect : 1.0)
                .DefaultIfEmpty(1.0)
                .ToList();

            double HeroScore(Photo photo)
            {
                var aspect = SmartCrop.AspectOf(photo);
                var penalty = slotAspects.Min(a => Math.Min(1.0, Math.Abs(Math.Log(aspect / a)) / Math.Log(3.0)));
                return DemandModel.TierRank(photo.EffectiveTier) + 1.5 * penalty;
            }

            return all
                .OrderBy(HeroScore)
                .ThenByDescending(p => p.Quality?.Fused ?? 0)
                .ThenByDescending(p => dayWeight.GetValueOrDefault(p.Id))
                .ThenBy(p => LayoutRandom.Unit(_request.Seed, "hero|" + p.ContentHash))
                .ThenBy(p => p.Id, StringComparer.Ordinal)
                .ToList();
        }

        private static IReadOnlyList<LayoutDay> RemovePhotos(IReadOnlyList<LayoutDay> days, HashSet<string> remove)
        {
            var result = new List<LayoutDay>(days.Count);
            foreach (var day in days)
            {
                var kept = day.Photos.Where(p => !remove.Contains(p.Id)).ToList();
                if (kept.Count == day.Photos.Count)
                {
                    result.Add(day);
                    continue;
                }

                if (kept.Count == 0 && day.Entries.Count == 0) continue;
                result.Add(day with { Photos = kept });
            }

            return result;
        }

        // ── Runs: where regenerated pages go between anchors ──────────────────────────────────

        private sealed record RunLayout(
            List<object> Items,
            List<int> InsertedAfterPages,
            int TotalPlannedPages);

        private sealed record RunSlot(int Index)
        {
            public List<LayoutDay> Days { get; } = [];

            public List<PagePlan> Plans { get; } = [];

            public bool CarriesTitlePage { get; set; }
        }

        private RunLayout BuildRunLayout(
            IReadOnlyList<Page> existing, bool[] keep, IReadOnlyList<LayoutDay> days, PagePlan? titlePlan)
        {
            var items = new List<object>();
            var runs = new List<RunSlot>();
            var insertedAfter = new List<int>();

            if (_request.Scope is LayoutScope.InsertUnplaced)
            {
                // Splice each produced page immediately after the last existing page holding a photo of
                // an earlier-or-equal date (doc 08 §10.3); existing pages are untouched.
                var byPosition = new SortedDictionary<int, List<LayoutDay>>();
                foreach (var day in days)
                {
                    var after = LastPageBefore(existing, day.Date);
                    if (!byPosition.TryGetValue(after, out var bucket))
                    {
                        bucket = [];
                        byPosition[after] = bucket;
                    }

                    bucket.Add(day);
                }

                if (byPosition.TryGetValue(-1, out var leading))
                {
                    var slot = NewRun(runs, leading);
                    items.Add(slot);
                    insertedAfter.Add(0);
                }

                for (var i = 0; i < existing.Count; i++)
                {
                    items.Add(existing[i]);
                    if (!byPosition.TryGetValue(i, out var bucket)) continue;
                    items.Add(NewRun(runs, bucket));
                    insertedAfter.Add(i + 1);
                }

                return Finish(items, runs, insertedAfter, days, titlePlan: null);
            }

            var anyRun = false;
            for (var i = 0; i < existing.Count; i++)
            {
                if (keep[i])
                {
                    items.Add(existing[i]);
                    continue;
                }

                if (items.Count == 0 || items[^1] is not RunSlot)
                {
                    items.Add(NewRun(runs, null));
                    anyRun = true;
                }
            }

            if (!anyRun && (days.Count > 0 || titlePlan is not null))
            {
                items.Add(NewRun(runs, null));
            }

            return Finish(items, runs, insertedAfter, days, titlePlan);
        }

        private static RunSlot NewRun(List<RunSlot> runs, List<LayoutDay>? days)
        {
            var slot = new RunSlot(runs.Count);
            if (days is not null) slot.Days.AddRange(days);
            runs.Add(slot);
            return slot;
        }

        private RunLayout Finish(
            List<object> items, List<RunSlot> runs, List<int> insertedAfter,
            IReadOnlyList<LayoutDay> days, PagePlan? titlePlan)
        {
            if (runs.Count == 0)
            {
                foreach (var day in days)
                {
                    foreach (var photo in day.Photos)
                    {
                        _diagnostics.Add(new LayoutDiagnostic
                        {
                            Kind = LayoutDiagnosticKind.UnplaceablePhoto,
                            Severity = LayoutSeverity.Warning,
                            Message = "Every page in scope is pinned, so this photo stays in the Unplaced bin.",
                            PhotoId = photo.Id,
                            Date = day.Date,
                        });
                    }
                }

                return new RunLayout(items, insertedAfter, 0);
            }

            if (_request.Scope is not LayoutScope.InsertUnplaced)
            {
                AssignDaysToRuns(items, runs, days);
            }

            if (titlePlan is not null) runs[0].CarriesTitlePage = true;

            // Phases 2–3 per run, so page counts are known before anything is rendered.
            var total = 0;
            var pageIndex = 0;
            foreach (var item in items)
            {
                if (item is Page)
                {
                    pageIndex++;
                    continue;
                }

                var run = (RunSlot)item;
                var startIndex = pageIndex;
                if (run.CarriesTitlePage && titlePlan is not null)
                {
                    run.Plans.Add(titlePlan with { Side = SideOf(startIndex) });
                    startIndex++;
                }

                var plans = PlanRun(run.Days, SideOf(startIndex), startIndex);
                run.Plans.AddRange(plans);
                pageIndex = startIndex + plans.Count;
                total += run.Plans.Count;
            }

            return new RunLayout(items, insertedAfter, total);
        }

        private void AssignDaysToRuns(List<object> items, List<RunSlot> runs, IReadOnlyList<LayoutDay> days)
        {
            if (runs.Count == 1)
            {
                runs[0].Days.AddRange(days);
                return;
            }

            // Bracket each run by the dates of the anchor pages around it, then place every day in the
            // run whose chronological window contains it — anchors partition the DP (doc 08 §5).
            var lower = new DateOnly?[runs.Count];
            var upper = new DateOnly?[runs.Count];

            DateOnly? runningLower = null;
            foreach (var item in items)
            {
                if (item is Page page)
                {
                    var dates = PageDates(page);
                    if (dates.Count > 0)
                    {
                        var max = dates.Max();
                        runningLower = runningLower is null || max > runningLower ? max : runningLower;
                    }

                    continue;
                }

                lower[((RunSlot)item).Index] = runningLower;
            }

            DateOnly? runningUpper = null;
            for (var i = items.Count - 1; i >= 0; i--)
            {
                if (items[i] is Page page)
                {
                    var dates = PageDates(page);
                    if (dates.Count > 0)
                    {
                        var min = dates.Min();
                        runningUpper = runningUpper is null || min < runningUpper ? min : runningUpper;
                    }

                    continue;
                }

                upper[((RunSlot)items[i]).Index] = runningUpper;
            }

            foreach (var day in days)
            {
                var chosen = -1;
                for (var r = 0; r < runs.Count; r++)
                {
                    var lo = lower[r];
                    var hi = upper[r];
                    if ((lo is null || day.Date >= lo) && (hi is null || day.Date <= hi))
                    {
                        chosen = r;
                        break;
                    }
                }

                if (chosen < 0)
                {
                    // No window contains it: take the run whose window is nearest in days.
                    var bestDistance = int.MaxValue;
                    for (var r = 0; r < runs.Count; r++)
                    {
                        var distance = Distance(day.Date, lower[r], upper[r]);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            chosen = r;
                        }
                    }
                }

                runs[Math.Max(0, chosen)].Days.Add(day);
            }

            foreach (var run in runs)
            {
                run.Days.Sort(static (a, b) => a.Date.CompareTo(b.Date));
            }
        }

        private static int Distance(DateOnly date, DateOnly? lower, DateOnly? upper)
        {
            if (lower is { } lo && date < lo) return lo.DayNumber - date.DayNumber;
            if (upper is { } hi && date > hi) return date.DayNumber - hi.DayNumber;
            return 0;
        }

        private HashSet<DateOnly> PageDates(Page page)
        {
            var dates = new HashSet<DateOnly>();
            foreach (var placement in page.Placements)
            {
                if (_photosById.TryGetValue(placement.PhotoId, out var photo)) dates.Add(photo.TakenOn);
            }

            return dates;
        }

        private int LastPageBefore(IReadOnlyList<Page> pages, DateOnly date)
        {
            var result = -1;
            for (var i = 0; i < pages.Count; i++)
            {
                var dates = PageDates(pages[i]);
                if (dates.Count == 0) continue;
                if (dates.Min() <= date) result = i;
            }

            return result;
        }

        // ── Phases 2–3 for one run, then 4–6 at emit time ─────────────────────────────────────

        private IReadOnlyList<PagePlan> PlanRun(List<LayoutDay> days, PageSide startParity, int startPageIndex)
        {
            if (days.Count == 0) return [];

            var segments = PagePartitioner.Partition(days, startParity, _weights, (first, count) =>
                MergeAllowed(days, first, count));

            var plans = new List<PagePlan>();
            var pageIndex = startPageIndex;

            foreach (var segment in segments)
            {
                var produced = ExpandSegment(days, segment, pageIndex);
                plans.AddRange(produced);
                pageIndex += produced.Count;
            }

            var parityOffset = _request.StartParity == PageSide.Right ? 1 : 0;
            return SpreadPromotion.Apply(
                plans, _context, startPageIndex + parityOffset, _memory.PagesSince(TemplateKind.SpreadPair));
        }

        private bool MergeAllowed(List<LayoutDay> days, int firstDayIndex, int count)
        {
            var slice = new List<LayoutDay>(count);
            var totalPhotos = 0;
            var demand = 0.0;

            for (var i = firstDayIndex; i < firstDayIndex + count; i++)
            {
                var day = days[i];
                if (day.Photos.Count > _weights.MaxPhotosPerMergedDay) return false;
                totalPhotos += day.Photos.Count;
                demand += DemandModel.Demand(day, _weights);
                slice.Add(day);
            }

            if (totalPhotos == 0 || totalPhotos > _weights.MaxSlotsPerPage) return false;
            if (demand > _weights.MaxMergeDemand) return false;

            // Only allow a merge phase 4 can actually satisfy: a multiDay template of exactly this
            // shape must exist and hold every day's text. Mirroring never changes slot sizes, so the
            // check is side-independent.
            foreach (var template in _catalog.MatchingMultiDay(slice))
            {
                var fits = true;
                var sections = template.Sections!;
                for (var i = 0; i < sections.Count && fits; i++)
                {
                    if (!slice[i].HasJournal) continue;
                    var chain = TemplateCatalog.SectionJournalChain(template, sections[i]);
                    fits = _context.FitsText(slice[i].Paragraphs, chain);
                }

                if (fits) return true;
            }

            return false;
        }

        private IReadOnlyList<PagePlan> ExpandSegment(List<LayoutDay> days, PageSegment segment, int startPageIndex)
        {
            if (segment.IsMerge)
            {
                var slice = new List<LayoutDay>(segment.DayCount);
                var photos = new List<Photo>();
                for (var i = segment.FirstDayIndex; i < segment.FirstDayIndex + segment.DayCount; i++)
                {
                    slice.Add(days[i]);
                    photos.AddRange(days[i].Photos);
                }

                var sections = slice.Select(d => new PagePlanSection(d, d.Photos)).ToList();
                var key = "md|" + string.Join('+', slice.Select(d => d.DayHash));

                return
                [
                    new PagePlan
                    {
                        Kind = PagePlanKind.MultiDay,
                        Photos = photos,
                        Sections = sections,
                        Entries = [],
                        Side = SideOf(startPageIndex),
                        Demand = segment.Demand,
                        PrimaryDate = slice[0].Date,
                        StableKey = key,
                    },
                ];
            }

            var day = days[segment.FirstDayIndex];
            var pages = Math.Max(1, segment.PageCount);

            // §12 escalation ladder step 2 — "shed photos to the next page of the same day". The
            // day's atomic text rides on its first page (§5), so the lever is that page's photo
            // count: only the counts in `feasible` have a template whose journal slot can hold this
            // much text, and the set is not an interval — the library's roomiest text column lives on
            // the 4-photo layouts, not the 1-photo ones.
            var feasible = day.HasJournal ? TextFeasibleCounts(day) : [];
            var firstMax = feasible.Count > 0 ? feasible[^1] : JournalBearingCeiling(day);

            // Adding a page only helps while the day is still on one page: from k ≥ 2 the boundary
            // nudge below already moves photos between the day's own pages, so repartitioning at k+1
            // would inflate the page count (a 40-photo day went from the doc's 7 pages to 9) and buy
            // nothing.
            if (day.HasJournal)
            {
                var attempts = 0;
                while (attempts < _weights.TextEscalationRetries &&
                       pages == 1 &&
                       pages < day.Photos.Count &&
                       day.Photos.Count > firstMax)
                {
                    pages++;
                    attempts++;
                }
            }

            var parts = PagePartitioner.SplitAtLargestGaps(day.Photos, pages, _weights, _request.Seed, firstMax);
            parts = NudgeFirstPageForText(day, parts, feasible);
            var plans = new List<PagePlan>(parts.Count);

            for (var i = 0; i < parts.Count; i++)
            {
                plans.Add(new PagePlan
                {
                    Kind = PagePlanKind.Standard,
                    Photos = parts[i],
                    Entries = i == 0 ? day.Entries : [],
                    Side = SideOf(startPageIndex + i),
                    Demand = segment.Demand / parts.Count,
                    PrimaryDate = day.Date,
                    StableKey = $"{day.DayHash}|{i}",
                    TextNeedsSpread = i == 0 && segment.TextNeedsSpread,
                });
            }

            if (segment.ParityViolated)
            {
                _diagnostics.Add(new LayoutDiagnostic
                {
                    Kind = LayoutDiagnosticKind.TextNeedsSpread,
                    Severity = LayoutSeverity.Warning,
                    Message = $"The journal entry for {day.Date:yyyy-MM-dd} wants a full Spread but the day " +
                              "starts on a right-hand page.",
                    Date = day.Date,
                });
            }

            return plans;
        }

        /// <summary>
        /// The most photos a page may hold and still have a journal slot at all. Used when no count
        /// can hold the day's text: the text then renders clipped and is reported (ladder step 4),
        /// which is far better than putting it on a page with nowhere to render.
        /// </summary>
        private int JournalBearingCeiling(LayoutDay day)
        {
            _ = day;
            return _catalog.MaxPhotoCountWithJournalSlot > 0
                ? Math.Min(_weights.MaxSlotsPerPage, _catalog.MaxPhotoCountWithJournalSlot)
                : _weights.MaxSlotsPerPage;
        }

        /// <summary>
        /// The photo counts, ascending, for which some single-page template's journal chain can hold
        /// this day's text (doc 08 §6's text hard filter). Empty when the entry fits nothing.
        /// </summary>
        private List<int> TextFeasibleCounts(LayoutDay day)
        {
            var counts = new List<int>();
            var ceiling = JournalBearingCeiling(day);
            for (var count = 1; count <= ceiling; count++)
            {
                if (TextFeasible(count, day.Paragraphs)) counts.Add(count);
            }

            return counts;
        }

        /// <summary>
        /// Moves the boundary between the day's first two pages so the text-bearing first page lands
        /// on a photo count some template can actually hold the text with (doc 08 §12 step 2). Only
        /// that one boundary moves; the rest of the day re-cuts at its own largest time gaps, so the
        /// clustering of §5 is preserved everywhere else.
        /// </summary>
        private IReadOnlyList<IReadOnlyList<Photo>> NudgeFirstPageForText(
            LayoutDay day, IReadOnlyList<IReadOnlyList<Photo>> parts, List<int> feasible)
        {
            if (feasible.Count == 0 || parts.Count < 2) return parts;

            var current = parts[0].Count;
            if (feasible.Contains(current)) return parts;

            var tailPages = parts.Count - 1;
            var total = day.Photos.Count;

            var target = feasible
                .Where(f => f < total)
                .Where(f => total - f >= tailPages && total - f <= tailPages * _weights.MaxSlotsPerPage)
                .OrderBy(f => Math.Abs(f - current))
                .ThenByDescending(f => f)
                .FirstOrDefault();

            if (target <= 0 || target == current) return parts;

            var head = new List<Photo>(target);
            for (var i = 0; i < target; i++) head.Add(day.Photos[i]);
            var rest = new List<Photo>(total - target);
            for (var i = target; i < total; i++) rest.Add(day.Photos[i]);

            var tail = PagePartitioner.SplitAtLargestGaps(rest, tailPages, _weights, _request.Seed);
            var result = new List<IReadOnlyList<Photo>>(parts.Count) { head };
            result.AddRange(tail);
            return result;
        }

        private bool TextFeasible(int photoCount, IReadOnlyList<string> paragraphs)
        {
            foreach (var template in _catalog.SinglePage(photoCount))
            {
                if (_context.FitsText(paragraphs, TemplateCatalog.JournalChain(template))) return true;
            }

            return false;
        }

        // ── Emit: phases 4–6, in final page order so pacing memory is correct ─────────────────

        private IReadOnlyList<Page> Emit(RunLayout layout, out int generated)
        {
            var pages = new List<Page>();
            generated = 0;

            foreach (var item in layout.Items)
            {
                if (item is Page anchor)
                {
                    pages.Add(anchor);
                    var template = anchor.DetachedTemplate ?? _catalog.Find(anchor.TemplateRef);
                    _memory.Record(anchor.TemplateRef, template?.Kind ?? TemplateKind.Standard);
                    continue;
                }

                var run = (RunSlot)item;
                var produced = new List<(PagePlan Plan, Page Page, TemplateChoice Choice)>();

                foreach (var plan in run.Plans)
                {
                    var sided = plan.Side == SideOf(pages.Count) ? plan : plan with { Side = SideOf(pages.Count) };
                    var choice = TemplateSelector.Select(sided, _context, _memory)
                                 ?? TemplateSelector.Select(sided, _context, _memory, allowTextOverflow: true);

                    if (choice is null)
                    {
                        foreach (var photo in sided.Photos)
                        {
                            _diagnostics.Add(new LayoutDiagnostic
                            {
                                Kind = LayoutDiagnosticKind.UnplaceablePhoto,
                                Severity = LayoutSeverity.Warning,
                                Message = $"No template holds {sided.Photos.Count} photo(s) for {sided.PrimaryDate:yyyy-MM-dd}.",
                                PhotoId = photo.Id,
                                Date = sided.PrimaryDate,
                            });
                        }

                        _diagnostics.Add(new LayoutDiagnostic
                        {
                            Kind = LayoutDiagnosticKind.NoTemplate,
                            Severity = LayoutSeverity.Error,
                            Message = $"No template passed the hard filters for {sided.PrimaryDate:yyyy-MM-dd}.",
                            Date = sided.PrimaryDate,
                        });
                        continue;
                    }

                    generated++;
                    var pageId = LayoutRandom.PageId(
                        _request.Seed,
                        string.Create(CultureInfo.InvariantCulture,
                            $"{_chapter.Year:D4}-{_chapter.Month:D2}|{run.Index}|{sided.StableKey}|{choice.Library.Id}"));

                    var firstDiagnostic = _diagnostics.Count;
                    var page = PageBuilder.Build(sided, choice, _context, pageId, _diagnostics);
                    pages.Add(page);

                    // Stamp the page number onto everything this page reported, so preflight and the
                    // R16 modal can name the page (doc 12).
                    for (var d = firstDiagnostic; d < _diagnostics.Count; d++)
                    {
                        _diagnostics[d] = _diagnostics[d] with { PageNumber = pages.Count };
                    }

                    produced.Add((sided, page, choice));
                    _memory.Record(choice.Library.Id, choice.Library.Kind);
                }

                ChainOverflowText(produced);
            }

            return pages;
        }

        /// <summary>
        /// §12 escalation ladder step 3: a day whose text still overflows its page continues into the
        /// facing page of the same Spread when that page belongs to the same day and has a journal
        /// slot. The text stays atomic and never crosses a Spread (kernel §9).
        /// </summary>
        private void ChainOverflowText(List<(PagePlan Plan, Page Page, TemplateChoice Choice)> produced)
        {
            for (var i = 0; i < produced.Count - 1; i++)
            {
                var (plan, page, choice) = produced[i];
                if (choice.TextFits || plan.JournalChars == 0) continue;

                // A JournalAssignment binds *whole entries* to a slot and carries no continuation
                // offset, so a day whose overflow is one single entry cannot be continued anywhere:
                // repeating its ids on the facing page would reprint the entry from the top rather
                // than carry on. Such a day stops at ladder step 4 — TextOverflow, which preflight
                // blocks on with editorial fixes (doc 12). Splitting mid-entry needs a schema field
                // (doc 04 §5) that v1 does not have.
                if (plan.Entries.Count < 2) continue;

                var (nextPlan, nextPage, nextChoice) = produced[i + 1];
                if (nextPlan.PrimaryDate != plan.PrimaryDate) continue;
                if (plan.Side != PageSide.Left || nextPlan.Side != PageSide.Right) continue;

                var chain = TemplateCatalog.JournalChain(nextChoice.Oriented);
                if (chain.Count == 0) continue;

                // Move the smallest suffix of the day's entries that lets the rest fit: every entry
                // stays whole and in date order, the day's text stays inside one Spread (kernel §9),
                // and nothing is printed twice.
                var pageChain = TemplateCatalog.JournalChain(choice.Oriented);
                var keep = plan.Entries.Count - 1;
                for (; keep >= 1; keep--)
                {
                    var paragraphs = plan.Entries.Take(keep).SelectMany(e => e.Paragraphs).ToList();
                    if (_context.FitsText(paragraphs, pageChain)) break;
                }

                if (keep < 1) keep = 1;
                var kept = plan.Entries.Take(keep).Select(e => e.Id).ToList();
                var moved = plan.Entries.Skip(keep).Select(e => e.Id).ToList();
                if (moved.Count == 0) continue;

                foreach (var assignment in page.JournalAssignments) assignment.EntryIds = [.. kept];

                nextPage.JournalAssignments.Add(new JournalAssignment
                {
                    TextSlotId = chain[0].Id,
                    EntryIds = moved,
                });

                _diagnostics.Add(new LayoutDiagnostic
                {
                    Kind = LayoutDiagnosticKind.TextNeedsSpread,
                    Severity = LayoutSeverity.Info,
                    Message = $"The journal text for {plan.PrimaryDate:yyyy-MM-dd} continues on the facing page.",
                    Date = plan.PrimaryDate,
                });
            }
        }

        /// <summary>
        /// The Unplaced bin after this run, in chronological order: every Chapter photo that ended up
        /// on no page. Placed ∪ Unplaced ∪ Excluded = the Chapter, and the three sets are disjoint
        /// (doc 13's conservation property, R10/R13/R17).
        /// </summary>
        private static IReadOnlyList<string> OrderedUnplaced(IReadOnlyList<Photo> photos, IReadOnlySet<string> placed)
        {
            var unplaced = new List<string>();
            foreach (var photo in photos)
            {
                if (placed.Contains(photo.Id)) continue;
                unplaced.Add(photo.Id);
            }

            return unplaced;
        }
    }
}
