using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;
using PhotoBook.Imaging.Auto;

namespace PhotoBook.Tests;

/// <summary>
/// Who auto-adjust may touch. This is the part of the feature that cannot be allowed to be wrong:
/// getting it wrong means silently overwriting edits someone made by hand.
/// </summary>
public sealed class AutoAdjustSelectionTests
{
    private static Photo Untouched(string id = "ph-untouched") => new()
    {
        Id = id,
        ContentHash = new string('a', 64),
        OriginalFileName = id + ".jpg",
    };

    private static Photo HandEdited()
    {
        var photo = Untouched("ph-hand");
        photo.Adjustments.Contrast = 0.4;
        photo.AdjustmentsUserEdited = true;
        return photo;
    }

    private static Photo AutoAdjusted(LookProfile look, double exposure = 0.3)
    {
        var photo = Untouched("ph-auto");
        photo.Adjustments.ExposureEv = exposure;
        photo.AutoAdjust = new AutoAdjustStamp
        {
            RulesVersion = AutoAdjustRules.VersionFor(look),
            SourceHash = photo.ContentHash,
            Measurement = new AutoAdjustMeasurement { MedianLuma = 0.30 },
        };

        return photo;
    }

    // ---- the three rules ---------------------------------------------------------------------------

    [Fact]
    public void AnUntouchedPhotoIsAdjusted()
    {
        Assert.Equal(AdjustmentOrigin.Untouched, Untouched().AdjustmentOrigin);
        Assert.True(AutoAdjustSelection.IsInScope(Untouched(), includeManual: false));
    }

    [Fact]
    public void AHandEditedPhotoIsSkipped()
    {
        var photo = HandEdited();

        Assert.Equal(AdjustmentOrigin.Manual, photo.AdjustmentOrigin);
        Assert.False(AutoAdjustSelection.IsInScope(photo, includeManual: false));

        // ...unless the user explicitly opts them in.
        Assert.True(AutoAdjustSelection.IsInScope(photo, includeManual: true));
    }

    [Fact]
    public void AnAutoAdjustedPhotoIsAdjustedAgain()
    {
        var photo = AutoAdjusted(new LookProfile());

        Assert.Equal(AdjustmentOrigin.Automatic, photo.AdjustmentOrigin);
        Assert.True(AutoAdjustSelection.IsInScope(photo, includeManual: false));
    }

    // ---- "update that photo with the new rules" ----------------------------------------------------

    [Fact]
    public void ChangingTheLookMakesEveryAutomaticPhotoOutOfDate()
    {
        var original = new LookProfile();
        var photo = AutoAdjusted(original);

        Assert.True(AutoAdjustSelection.IsUpToDate(photo, AutoAdjustRules.VersionFor(original)));

        var warmer = new LookProfile { Warmth = 0.4 };
        Assert.False(AutoAdjustSelection.IsUpToDate(photo, AutoAdjustRules.VersionFor(warmer)));
    }

    [Fact]
    public void ReRunningWithUnchangedRulesTouchesNothing()
    {
        // The second click of the button must be free. Every write mints new thumbnail keys and
        // abandons the old files, so rewriting identical numbers is not harmless.
        var look = new LookProfile();
        var photo = AutoAdjusted(look);

        var stamp = new AutoAdjustStamp
        {
            RulesVersion = AutoAdjustRules.VersionFor(look),
            SourceHash = photo.ContentHash,
            Measurement = photo.AutoAdjust!.Measurement,
        };

        Assert.False(AutoAdjustSelection.NeedsWrite(photo, photo.Adjustments with { }, stamp));
    }

    [Fact]
    public void AReImportedPhotoIsMeasuredAgainRatherThanTrusted()
    {
        var look = new LookProfile();
        var photo = AutoAdjusted(look);

        // The bytes were replaced by a re-import. The stored measurement describes pixels that no
        // longer exist, so re-deriving from it would apply the old photo's correction to the new one.
        photo.ContentHash = new string('b', 64);

        Assert.False(AutoAdjustSelection.IsUpToDate(photo, AutoAdjustRules.VersionFor(look)));
        Assert.Null(AutoAdjustSelection.ReusableMeasurement(photo));
    }

    [Fact]
    public void AnUnchangedPhotoReusesItsMeasurementSoARerunNeedsNoDecode()
    {
        var photo = AutoAdjusted(new LookProfile());

        var reusable = AutoAdjustSelection.ReusableMeasurement(photo);

        Assert.NotNull(reusable);
        Assert.Equal(0.30, reusable!.MedianLuma, 6);
    }

    // ---- the migration hazard ----------------------------------------------------------------------

    [Fact]
    public void EditsMadeBeforeThisFeatureExistedAreTreatedAsHandEdits()
    {
        // A photos.json written by an older build has neither a stamp nor the flag. Its adjustments
        // can only have come from a human, and reading them as anything else means the first click
        // of "Auto-adjust all" destroys them.
        var legacy = Untouched("ph-legacy");
        legacy.Adjustments.Brightness = 0.5;
        legacy.AutoAdjust = null;
        legacy.AdjustmentsUserEdited = false;

        Assert.Equal(AdjustmentOrigin.Manual, legacy.AdjustmentOrigin);
        Assert.False(AutoAdjustSelection.IsInScope(legacy, includeManual: false));
    }

    [Fact]
    public void DeliberatelyResettingAPhotoKeepsItOffAuto()
    {
        // Identity adjustments plus the flag: "I looked at this one and decided it needs nothing."
        // Without the flag it would be indistinguishable from a photo nobody has opened.
        var reset = Untouched("ph-reset");
        reset.AdjustmentsUserEdited = true;

        Assert.True(reset.Adjustments.IsIdentity);
        Assert.Equal(AdjustmentOrigin.Manual, reset.AdjustmentOrigin);
        Assert.False(AutoAdjustSelection.IsInScope(reset, includeManual: false));
    }

    [Fact]
    public void ExcludedAndUndecodableRowsAreNeverInScope()
    {
        var excluded = Untouched("ph-excluded");
        excluded.Excluded = true;

        var broken = Untouched("ph-broken");
        broken.DecodeFailed = true;

        Assert.False(AutoAdjustSelection.IsInScope(excluded, includeManual: true));
        Assert.False(AutoAdjustSelection.IsInScope(broken, includeManual: true));
    }

    // ---- the plan the confirmation shows -----------------------------------------------------------

    [Fact]
    public void ThePlanCountsEachPhotoOnceAndTellsTheUserWhatWillBeSkipped()
    {
        var look = new LookProfile();
        var excluded = Untouched("ph-excluded");
        excluded.Excluded = true;

        var plan = AutoAdjustSelection.Describe(
            [Untouched("a"), Untouched("b"), HandEdited(), AutoAdjusted(look), excluded], look);

        Assert.Equal(2, plan.Untouched);
        Assert.Equal(1, plan.Manual);
        Assert.Equal(1, plan.Automatic);
        Assert.Equal(1, plan.UpToDate);

        Assert.Equal(3, plan.Scope(includeManual: false));
        Assert.Equal(4, plan.Scope(includeManual: true));

        // The one automatic photo is already current, so only the two untouched ones need decoding.
        Assert.Equal(2, plan.Work(includeManual: false));
    }

    [Fact]
    public void ChangingTheLookPutsEveryAutomaticPhotoBackIntoTheWorkList()
    {
        var look = new LookProfile();
        var photos = new[] { AutoAdjusted(look), Untouched() };

        Assert.Equal(1, AutoAdjustSelection.Describe(photos, look).Work(includeManual: false));

        var brighter = new LookProfile { Brightness = 0.5 };
        var plan = AutoAdjustSelection.Describe(photos, brighter);

        Assert.Equal(0, plan.UpToDate);
        Assert.Equal(2, plan.Work(includeManual: false));
    }

    // ---- persistence -------------------------------------------------------------------------------

    [Fact]
    public void ProvenanceSurvivesASaveAndLoad()
    {
        var look = new LookProfile { Warmth = 0.25, Strength = 0.9 };
        var catalog = new PhotoCatalog();
        catalog.Photos.Add(AutoAdjusted(look));
        catalog.Photos.Add(HandEdited());
        catalog.Photos.Add(Untouched());

        var round = ProjectJson.Deserialize<PhotoCatalog>(ProjectJson.Serialize(catalog));

        Assert.Equal(AdjustmentOrigin.Automatic, round.Photos[0].AdjustmentOrigin);
        Assert.Equal(AutoAdjustRules.VersionFor(look), round.Photos[0].AutoAdjust!.RulesVersion);
        Assert.Equal(0.30, round.Photos[0].AutoAdjust!.Measurement!.MedianLuma, 6);

        Assert.Equal(AdjustmentOrigin.Manual, round.Photos[1].AdjustmentOrigin);
        Assert.Equal(AdjustmentOrigin.Untouched, round.Photos[2].AdjustmentOrigin);
    }

    [Fact]
    public void APhotoRowKeepsMembersWrittenByANewerBuild()
    {
        // Without JsonExtensionData on Photo, an older build would drop a photo's provenance on every
        // save, and the next auto-adjust run would overwrite hand edits it could no longer see.
        const string Json = """
            {
              "schemaVersion": 1,
              "photos": [
                { "id": "ph-1", "contentHash": "aa", "somethingFromTheFuture": { "keep": true } }
              ]
            }
            """;

        var round = ProjectJson.Deserialize<PhotoCatalog>(ProjectJson.Serialize(
            ProjectJson.Deserialize<PhotoCatalog>(Json)));

        Assert.Contains("somethingFromTheFuture", ProjectJson.Serialize(round), StringComparison.Ordinal);
    }

    [Fact]
    public void TwoSavesOfTheSameModelAreByteIdentical()
    {
        // doc 04 §4 rule 5. A timestamp in the stamp would break this on every run.
        var catalog = new PhotoCatalog();
        catalog.Photos.Add(AutoAdjusted(new LookProfile()));

        Assert.Equal(ProjectJson.Serialize(catalog), ProjectJson.Serialize(catalog));
    }

    [Fact]
    public void TheLookProfileRoundTripsOnTheBook()
    {
        var book = new Book { Look = new LookProfile { Strength = 0.5, Warmth = -0.2, Straighten = false } };

        var round = ProjectJson.Deserialize<Book>(ProjectJson.Serialize(book));

        Assert.Equal(0.5, round.Look.Strength, 6);
        Assert.Equal(-0.2, round.Look.Warmth, 6);
        Assert.False(round.Look.Straighten);
        Assert.False(round.Look.AdjustOnImport);
    }

    [Fact]
    public void ABookWrittenBeforeLookSettingsExistedGetsTheDefaults()
    {
        var round = ProjectJson.Deserialize<Book>("""{ "schemaVersion": 1, "id": "bk-1" }""");

        Assert.NotNull(round.Look);
        Assert.Equal(LookProfile.DefaultStrength, round.Look.Strength, 6);
        Assert.True(round.Look.Straighten);
        Assert.True(round.Look.IsDefault);
    }
}
