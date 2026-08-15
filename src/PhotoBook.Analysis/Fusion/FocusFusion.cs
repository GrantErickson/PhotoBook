using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Fusion;

/// <summary>
/// The analyzer-independent focus fusion of doc 06 and kernel §4. Analyzers detect; fusion decides.
/// Priority is <c>user &gt; person &gt; face &gt; saliency</c> — human intent beats a name, a name
/// beats an anonymous face, any face beats "something contrasty" — and the steps run in exactly the
/// documented order:
/// <list type="number">
/// <item><description>inject <see cref="FocusKind.Person"/> regions from OneDrive person tags that carry a rect;</description></item>
/// <item><description>drop faces that duplicate a person region (IoU &gt; 0.3) — same face, but the person region carries the name;</description></item>
/// <item><description>merge same-kind regions overlapping by IoU &gt; 0.4 into their union, keeping the max weight;</description></item>
/// <item><description>apply user edits: user-drawn regions win, and suppressed derived regions stay deleted;</description></item>
/// <item><description>pick the primary: highest weight within the highest non-empty priority kind.</description></item>
/// </list>
/// </summary>
public static class FocusFusion
{
    /// <summary>Weight given to a named person from a OneDrive tag (doc 06 step 1).</summary>
    public const double PersonWeight = 0.95;

    /// <summary>Weight of a user-drawn region — the top of the scale, by definition (doc 06 step 4).</summary>
    public const double UserWeight = 1.0;

    /// <summary>A face overlapping a person region by more than this IoU is the same face (doc 06 step 2).</summary>
    public const double PersonFaceDuplicateIoU = 0.30;

    /// <summary>Same-kind regions overlapping by more than this IoU are one subject (doc 06 step 3).</summary>
    public const double SameKindMergeIoU = 0.40;

    /// <summary>A derived region overlapping a suppression by more than this IoU is the region the user deleted.</summary>
    public const double SuppressionMatchIoU = 0.50;

    /// <summary>Runs the five fusion steps.</summary>
    /// <param name="input">Derived regions, person tags, user regions and suppressions.</param>
    public static FocusFusionResult Fuse(FocusFusionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Step 1 — inject person regions for every tag that carries a rect. Tags without a rect
        // cannot be localized; they only contribute the quality bonus in QualityFusion.
        var persons = new List<FocusRegion>();
        foreach (var tag in input.PersonTags)
        {
            if (tag.RegionRect is not { } rect) continue;
            var clamped = ClampToUnitSquare(rect);
            if (!clamped.IsWellFormed) continue;
            persons.Add(new FocusRegion
            {
                Rect = clamped,
                Weight = PersonWeight,
                Kind = FocusKind.Person,
                PersonName = string.IsNullOrWhiteSpace(tag.Name) ? null : tag.Name,
            });
        }

        var faces = new List<FocusRegion>();
        var saliency = new List<FocusRegion>();
        foreach (var region in input.Derived)
        {
            if (region is null) continue;
            var clamped = ClampToUnitSquare(region.Rect);
            if (!clamped.IsWellFormed) continue;
            var copy = new FocusRegion
            {
                Rect = clamped,
                Weight = Math.Clamp(region.Weight, 0, 1),
                Kind = region.Kind,
                PersonName = region.PersonName,
            };

            switch (region.Kind)
            {
                case FocusKind.Face:
                    faces.Add(copy);
                    break;
                case FocusKind.Person:
                    persons.Add(copy);
                    break;
                case FocusKind.User:
                    // A "user" region can only come from photos.json, never from an analyzer.
                    break;
                default:
                    saliency.Add(copy);
                    break;
            }
        }

        // Step 2 — dedupe face against person.
        if (persons.Count > 0 && faces.Count > 0)
            faces = faces.Where(f => !persons.Any(p => p.Rect.IoU(f.Rect) > PersonFaceDuplicateIoU)).ToList();

        // Step 3 — merge within kind.
        persons = MergeWithinKind(persons);
        faces = MergeWithinKind(faces);
        saliency = MergeWithinKind(saliency);

        // Step 4 — user edits. Suppressions kill derived regions; user regions are added untouched.
        if (input.Suppressions.Count > 0)
        {
            persons = ApplySuppressions(persons, input.Suppressions);
            faces = ApplySuppressions(faces, input.Suppressions);
            saliency = ApplySuppressions(saliency, input.Suppressions);
        }

        var users = new List<FocusRegion>();
        foreach (var region in input.UserRegions)
        {
            if (region is null || region.Kind != FocusKind.User) continue;
            var clamped = ClampToUnitSquare(region.Rect);
            if (!clamped.IsWellFormed) continue;
            users.Add(new FocusRegion
            {
                Rect = clamped,
                Weight = region.Weight > 0 ? Math.Clamp(region.Weight, 0, 1) : UserWeight,
                Kind = FocusKind.User,
                PersonName = region.PersonName,
            });
        }

        users = MergeWithinKind(users);

        var all = new List<FocusRegion>(users.Count + persons.Count + faces.Count + saliency.Count);
        all.AddRange(users);
        all.AddRange(persons);
        all.AddRange(faces);
        all.AddRange(saliency);

        // Deterministic order: priority first, then weight, then geometry — so photos.json is
        // byte-stable across runs (doc 04 §4 rule 5).
        all.Sort(CompareRegions);

        return new FocusFusionResult(all, SelectPrimary(all));
    }

    /// <summary>
    /// Fuses an analyzer's output for a photo, taking the user's regions from the photo itself. The
    /// caller supplies suppressions because Core's <see cref="Photo"/> has no suppression list yet
    /// (doc 06 step 4 describes one; until it exists, deleting a derived region is a UI concern).
    /// </summary>
    /// <param name="photo">The catalog record; its <see cref="FocusKind.User"/> regions and person tags are used.</param>
    /// <param name="analysis">What the analyzer proposed.</param>
    /// <param name="suppressions">Rects of derived regions the user deleted; optional.</param>
    public static FocusFusionResult FuseForPhoto(Photo photo, AnalysisResult analysis, IReadOnlyList<Rect>? suppressions = null)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(analysis);

        return Fuse(new FocusFusionInput(
            analysis.Regions,
            photo.PersonTags as IReadOnlyList<PersonTag> ?? photo.PersonTags.ToList(),
            photo.FocusRegions.Where(r => r.Kind == FocusKind.User).ToList(),
            suppressions ?? []));
    }

    /// <summary>The primary region: highest weight inside the highest non-empty priority kind (kernel §4).</summary>
    public static FocusRegion? SelectPrimary(IEnumerable<FocusRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        FocusRegion? best = null;
        foreach (var region in regions)
        {
            if (region is null) continue;
            if (best is null || CompareRegions(region, best) < 0) best = region;
        }

        return best;
    }

    private static int CompareRegions(FocusRegion a, FocusRegion b)
    {
        var byKind = a.KindPriority.CompareTo(b.KindPriority);
        if (byKind != 0) return byKind;
        var byWeight = b.Weight.CompareTo(a.Weight);
        if (byWeight != 0) return byWeight;
        var byArea = b.Rect.Area.CompareTo(a.Rect.Area);
        if (byArea != 0) return byArea;
        var byX = a.Rect.X.CompareTo(b.Rect.X);
        return byX != 0 ? byX : a.Rect.Y.CompareTo(b.Rect.Y);
    }

    private static List<FocusRegion> MergeWithinKind(List<FocusRegion> regions)
    {
        if (regions.Count < 2) return regions;

        var merged = true;
        while (merged)
        {
            merged = false;
            for (var i = 0; i < regions.Count && !merged; i++)
            {
                for (var j = i + 1; j < regions.Count; j++)
                {
                    if (regions[i].Rect.IoU(regions[j].Rect) <= SameKindMergeIoU) continue;

                    regions[i] = new FocusRegion
                    {
                        Rect = regions[i].Rect.Union(regions[j].Rect),
                        Weight = Math.Max(regions[i].Weight, regions[j].Weight),
                        Kind = regions[i].Kind,
                        PersonName = regions[i].PersonName ?? regions[j].PersonName,
                    };
                    regions.RemoveAt(j);
                    merged = true;
                    break;
                }
            }
        }

        return regions;
    }

    private static List<FocusRegion> ApplySuppressions(List<FocusRegion> regions, IReadOnlyList<Rect> suppressions) =>
        regions.Where(r => !suppressions.Any(s => s.IoU(r.Rect) > SuppressionMatchIoU)).ToList();

    private static Rect ClampToUnitSquare(Rect rect)
    {
        if (!double.IsFinite(rect.X) || !double.IsFinite(rect.Y) || !double.IsFinite(rect.W) || !double.IsFinite(rect.H))
            return default;

        var left = Math.Clamp(rect.X, 0, 1);
        var top = Math.Clamp(rect.Y, 0, 1);
        var right = Math.Clamp(rect.Right, 0, 1);
        var bottom = Math.Clamp(rect.Bottom, 0, 1);
        return Rect.FromEdges(left, top, right, bottom);
    }
}
