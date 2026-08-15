using System.Globalization;
using PhotoBook.Analysis.Fusion;
using PhotoBook.Core.Model;

namespace PhotoBook.Tests;

/// <summary>
/// The month-relative half of R26 (kernel §4, doc 06): rank a synthetic month and check the S/A/B/C
/// distribution lands on the documented 10 / 25 / 45 / 20 bands, that a month always has a top photo
/// and a bottom photo, and that the user's override is never touched.
/// </summary>
public sealed class TierAssignmentTests
{
    [Fact]
    public void ASyntheticMonthLandsOnTheKernelDistribution()
    {
        var photos = Month(20);

        var result = TierAssigner.AssignMonth(photos);

        Assert.Equal(20, result.Ranked);
        Assert.Equal(0, result.Skipped);

        // Percent rank is 100·(n−1−i)/(n−1); with n = 20 the kernel §4 bands cut it here.
        Assert.Equal(2, result.CountOf(Tier.S));    // top 10%
        Assert.Equal(5, result.CountOf(Tier.A));    // next 25%
        Assert.Equal(9, result.CountOf(Tier.B));    // next 45%
        Assert.Equal(4, result.CountOf(Tier.C));    // bottom 20%
        Assert.Equal(20, result.Counts.Values.Sum());
    }

    [Fact]
    public void TheBandsTrackTheIdealSharesAcrossAWholeYearOfMonthSizes()
    {
        foreach (var size in new[] { 40, 60, 100, 200 })
        {
            var result = TierAssigner.AssignMonth(Month(size));

            AssertShare(result.CountOf(Tier.S), size, 0.10, size);
            AssertShare(result.CountOf(Tier.A), size, 0.25, size);
            AssertShare(result.CountOf(Tier.B), size, 0.45, size);
            AssertShare(result.CountOf(Tier.C), size, 0.20, size);
        }

        static void AssertShare(int actual, int total, double ideal, int size)
        {
            var expected = ideal * total;
            Assert.True(
                Math.Abs(actual - expected) <= 1.5,
                string.Create(CultureInfo.InvariantCulture,
                    $"month of {size}: expected about {expected:F1} photos at {ideal:P0}, got {actual}"));
        }
    }

    [Fact]
    public void RankingIsMonotonicAndTheBestAndWorstPhotosAnchorTheBands()
    {
        var photos = Month(30);

        TierAssigner.AssignMonth(photos);

        var ordered = photos.OrderByDescending(p => p.Quality!.Fused).ToList();
        Assert.Equal(Tier.S, ordered[0].Tier);
        Assert.Equal(Tier.C, ordered[^1].Tier);
        Assert.Equal(100, ordered[0].Quality!.MonthPercentile);
        Assert.Equal(0, ordered[^1].Quality!.MonthPercentile);

        for (var i = 1; i < ordered.Count; i++)
        {
            Assert.True(ordered[i].Quality!.MonthPercentile <= ordered[i - 1].Quality!.MonthPercentile);
            Assert.True((int)ordered[i].Tier! >= (int)ordered[i - 1].Tier!);
        }
    }

    [Fact]
    public void ASinglePhotoMonthIsItsOwnTopPhoto()
    {
        var photos = Month(1);

        var result = TierAssigner.AssignMonth(photos);

        Assert.Equal(1, result.Ranked);
        Assert.Equal(100, photos[0].Quality!.MonthPercentile);
        Assert.Equal(Tier.S, photos[0].Tier);
    }

    [Fact]
    public void ExcludedAndUnanalyzedPhotosStayOutOfThePoolAndAreLeftAlone()
    {
        var photos = Month(10);
        photos[0].Excluded = true;                                  // R17 tombstone
        photos[1].Quality = null;                                   // not analyzed yet
        photos[1].Tier = null;

        var result = TierAssigner.AssignMonth(photos);

        Assert.Equal(8, result.Ranked);
        Assert.Equal(2, result.Skipped);
        Assert.Null(photos[1].Tier);
        Assert.Equal(8, photos.Count(p => p.Tier is not null));
    }

    [Fact]
    public void TheUserOverrideIsNeverWrittenClearedOrReDerived()
    {
        var photos = Month(12);
        var demoted = photos.OrderByDescending(p => p.Quality!.Fused).First();
        demoted.UserTierOverride = Tier.C;

        var result = TierAssigner.AssignMonth(photos);

        // The override is absolute for layout, but the photo keeps its computed tier for the
        // "S, demoted to C" display and stays in the pool (kernel §4).
        Assert.Equal(1, result.Overridden);
        Assert.Equal(Tier.C, demoted.UserTierOverride);
        Assert.Equal(Tier.S, demoted.Tier);
        Assert.Equal(Tier.C, demoted.EffectiveTier);
    }

    [Fact]
    public void MonthsAreRankedIndependentlyOfEachOther()
    {
        // A weak December photo must still be December's best; it never competes with July.
        var july = Month(10, month: 7, scoreBase: 0.80, id: "jul");
        var december = Month(10, month: 12, scoreBase: 0.10, id: "dec");

        var results = TierAssigner.AssignAllMonths([.. july, .. december]);

        Assert.Equal(2, results.Count);
        Assert.Contains(Tier.S, december.Select(p => p.Tier));
        Assert.Contains(Tier.C, july.Select(p => p.Tier));
    }

    [Fact]
    public void TiesAreBrokenDeterministicallySoTwoRunsAgree()
    {
        var first = Month(15, flat: true);
        var second = Month(15, flat: true);

        TierAssigner.AssignMonth(first);
        TierAssigner.AssignMonth(second);

        Assert.Equal(
            first.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => p.Tier),
            second.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => p.Tier));
    }

    private static List<Photo> Month(
        int count, int month = 3, double scoreBase = 0.0, string id = "ph", bool flat = false)
    {
        var photos = new List<Photo>(count);
        for (var i = 0; i < count; i++)
        {
            // Evenly spaced scores so the band arithmetic is exercised rather than a lucky clustering.
            var fused = flat ? 0.5 : scoreBase + (count == 1 ? 0.5 : 0.19 * i / (count - 1));
            photos.Add(new Photo
            {
                Id = string.Create(CultureInfo.InvariantCulture, $"{id}-{i:000}"),
                ContentHash = new string((char)('a' + i % 26), 64),
                TakenAt = new DateTime(2024, month, 1 + i % 28, 12, 0, 0),
                Quality = new QualityScore { Fused = fused, Aesthetic = fused, Sharpness = fused, Exposure = fused },
            });
        }

        return photos;
    }
}
