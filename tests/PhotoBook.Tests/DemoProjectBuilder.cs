using PhotoBook.Analysis.Composition;
using PhotoBook.Analysis.Running;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;
using PhotoBook.Engine;
using PhotoBook.Imaging;
using PhotoBook.Ingestion;
using PhotoBook.Rendering;
using PhotoBook.Tests.Fixtures;

namespace PhotoBook.Tests;

/// <summary>
/// Builds a realistic project on disk so the desktop app can be launched against actual content.
/// Skipped unless PHOTOBOOK_DEMO_OUT is set, so it never runs in a normal test pass.
/// </summary>
public sealed class DemoProjectBuilder
{
    [Fact]
    public async Task BuildDemoProject()
    {
        var root = Environment.GetEnvironmentVariable("PHOTOBOOK_DEMO_OUT");
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        var incoming = Path.Combine(root, "_incoming");
        Directory.CreateDirectory(incoming);

        // A month shaped like a real one: a few sparse days, one heavy day, varied aspects.
        var rng = new Random(20240607);
        var day = 1;
        var index = 0;
        foreach (var count in new[] { 3, 2, 2, 14, 4, 9, 3, 6, 2, 5 })
        {
            for (var i = 0; i < count; i++)
            {
                var landscape = rng.Next(3) != 0;
                var w = landscape ? 1600 : 1100;
                var h = landscape ? 1100 : 1600;
                var taken = new DateTime(2024, 6, Math.Min(day, 28), 8 + (i % 10), (i * 7) % 60, 0);

                SyntheticImages.Write(
                    Path.Combine(incoming, $"IMG_{index:0000}.jpg"),
                    new SyntheticImages.SceneSpec
                    {
                        Width = w,
                        Height = h,
                        ExifTaken = taken,
                        Subject = new Rect(rng.NextDouble() * 0.5, rng.NextDouble() * 0.45, 0.28, 0.32),
                        BlurSigma = rng.Next(9) == 0 ? 2.2 : 0,
                        ExposureScale = rng.Next(11) == 0 ? 1.45 : 1.0,
                    });
                index++;
            }

            day += 3;
        }

        var project = Path.Combine(root, "Family2024");
        await ProjectStore.CreateNewAsync(project, 2024, "Family 2024");

        var store = new ProjectStore(project);
        var loaded = await store.LoadAsync();
        var snapshot = loaded.Value;

        var importer = new PhotoImporter(store.Paths);
        var report = await importer.ImportAsync(new FolderPhotoSource(incoming), snapshot.Photos);

        var cache = new ThumbnailCache(
            store.Paths, ThumbnailSources.FromCatalog(store.Paths, () => snapshot.Photos));

        using var selection = AnalyzerFactory.CreateFor(snapshot.Book);
        var locator = new DelegateAnalysisCopyLocator((photo, ct) =>
            cache.GetOrCreateAsync(photo.ContentHash, ThumbnailTier.Analysis1024, ct));

        var photos = snapshot.Photos.Photos.Where(p => !p.Excluded).ToList();
        await new AnalysisRunner(selection.Analyzer, locator)
            .RunAsync(photos, new AnalysisRunOptions { TierPool = photos });

        // Lay out June through the same Skia measurer the app injects.
        var measurer = new SkiaTextMeasurer();
        var chapter = snapshot.Chapters.FirstOrDefault(c => c.Month == 6)
                      ?? new Chapter { Year = 2024, Month = 6 };

        var result = LayoutEngine.LayoutChapter(new LayoutRequest
        {
            Chapter = new ChapterInput
            {
                Year = 2024,
                Month = 6,
                Photos = snapshot.Photos.InChapter(2024, 6).Where(p => !p.Excluded).ToList(),
                JournalEntries = [],
            },
            Style = StyleResolver.Resolve(snapshot.Book, chapter, null),
            Seed = snapshot.Book.Seed,
            TextMeasurer = measurer,
        });

        chapter.Pages = [.. result.Pages];

        await store.SaveBookAsync(snapshot.Book);
        await store.SavePhotosAsync(snapshot.Photos);
        await store.SaveChapterAsync(chapter);

        // A Word journal sitting beside the project, for driving "Import journal…" by hand. It is
        // deliberately *not* imported here: the interesting surface is the import report, and it only
        // exists because some entries do not resolve — an undated heading, an unsure date, and one
        // entry from the wrong year (doc 11).
        SyntheticJournal.Write(Path.Combine(root, "journal-2024.docx"),
        [
            SyntheticJournal.Para.Heading("Saturday, June 1, 2024"),
            new SyntheticJournal.Para(
                "First proper weekend of the summer. We took the long way to the lake and nobody complained."),
            new SyntheticJournal.Para("June 4 — Sports day. Two skinned knees and one ribbon."),
            new SyntheticJournal.Para("6/7 — Rain all day, so we built the fort in the front room instead."),
            new SyntheticJournal.Para(
                "June 10–12 my sister came to stay and the kids barely slept the whole time."),
            new SyntheticJournal.Para("Monday the 17th we finally got the garden beds in."),
            SyntheticJournal.Para.Heading("Things I Keep Meaning To Write Down"),
            new SyntheticJournal.Para(
                "The way she says 'hopspital'. The way he holds the dog's collar when he is nervous."),
            new SyntheticJournal.Para("March 3, 2023 — the day we decided to make these books at all."),
        ]);

        // Warm the grid thumbnails so the app paints immediately on open.
        foreach (var photo in photos)
        {
            await cache.GetOrCreateAsync(photo.ContentHash, ThumbnailTier.Grid256);
        }

        cache.Dispose();

        Console.WriteLine($"demo project: {project}");
        Console.WriteLine($"imported={report.Entries.Count} photos={photos.Count} pages={result.Pages.Count}");
    }
}
