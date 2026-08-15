using PhotoBook.Core.Model;

namespace PhotoBook.Tests;

/// <summary>
/// The style cascade of doc 10 §2 (R23): global → chapter → page, merged <b>leaf by leaf</b>, so an
/// override changes only the field it sets and removing it restores inheritance with nothing left
/// behind.
/// </summary>
public class StyleResolverTests
{
    [Fact]
    public void WithNoOverrides_TheShippedDefaultsResolve()
    {
        var style = StyleResolver.Resolve(NewBook());

        Assert.Equal("Source Serif 4", style.JournalText!.Family);
        Assert.Equal(10.5, style.JournalText.SizePt);
        Assert.Equal("Source Sans 3", style.CaptionText!.Family);
        Assert.Equal(8.5, style.CaptionText.SizePt);
        Assert.Equal("Playfair Display", style.MonthTitle!.Family);
        Assert.Equal(64, style.MonthTitle.SizePt);
        Assert.Equal("#FFFFFF", style.JournalText.Color);
        Assert.Equal("#000000", style.Background!.Color);
        Assert.False(style.ImageBorder!.Enabled);
        Assert.True(style.OverlayScrim!.Enabled);
    }

    [Fact]
    public void AChapterOverride_ChangesOnlyTheOverriddenLeaf()
    {
        var book = NewBook();
        var chapter = new Chapter
        {
            Year = 2024,
            Month = 3,
            StyleOverride = new Style { CaptionText = new TextStyle { SizePt = 9.5 } },
        };

        var style = StyleResolver.Resolve(book, chapter);

        Assert.Equal(9.5, style.CaptionText!.SizePt);                    // the overridden leaf
        Assert.Equal("Source Sans 3", style.CaptionText.Family);         // siblings still inherited
        Assert.Equal("#FFFFFF", style.CaptionText.Color);
        Assert.Equal(1.25, style.CaptionText.LineHeight);
        Assert.Equal(10.5, style.JournalText!.SizePt);                   // other blocks untouched
        Assert.Equal("#000000", style.Background!.Color);
        Assert.Equal(0.6, style.OverlayScrim!.MaxOpacity);

        Assert.Equal(StyleLevel.Chapter, StyleResolver.LevelOf(s => s.CaptionText?.SizePt, book, chapter));
        Assert.Equal(StyleLevel.Book, StyleResolver.LevelOf(s => s.CaptionText?.Family, book, chapter));
    }

    [Fact]
    public void RemovingTheChapterOverride_RestoresInheritance()
    {
        var book = NewBook();
        var chapter = new Chapter
        {
            Year = 2024,
            Month = 3,
            StyleOverride = new Style { CaptionText = new TextStyle { SizePt = 9.5 } },
        };

        Assert.Equal(9.5, StyleResolver.Resolve(book, chapter).CaptionText!.SizePt);

        chapter.StyleOverride = null; // the user cleared the override

        var restored = StyleResolver.Resolve(book, chapter);
        Assert.Equal(8.5, restored.CaptionText!.SizePt);
        Assert.Equal("Source Sans 3", restored.CaptionText.Family);
        Assert.Equal(StyleLevel.Book, StyleResolver.LevelOf(s => s.CaptionText?.SizePt, book, chapter));
    }

    [Fact]
    public void ChangingTheBookLevel_FlowsThroughAnUnrelatedChapterOverride()
    {
        var book = NewBook();
        book.Style.CaptionText!.Color = "#EEEEEE";
        var chapter = new Chapter { StyleOverride = new Style { CaptionText = new TextStyle { SizePt = 9.5 } } };

        var style = StyleResolver.Resolve(book, chapter);

        Assert.Equal("#EEEEEE", style.CaptionText!.Color); // inherited from the book, not snapshotted
        Assert.Equal(9.5, style.CaptionText.SizePt);       // still overridden by the chapter
    }

    [Fact]
    public void APageOverride_BeatsTheChapterWhichBeatsTheBook()
    {
        var book = NewBook();
        book.Style.JournalText!.SizePt = 11.0;
        var chapter = new Chapter { StyleOverride = new Style { JournalText = new TextStyle { SizePt = 12.0 } } };
        var page = new Page { StyleOverride = new Style { JournalText = new TextStyle { SizePt = 13.0 } } };

        Assert.Equal(11.0, StyleResolver.Resolve(book).JournalText!.SizePt);
        Assert.Equal(12.0, StyleResolver.Resolve(book, chapter).JournalText!.SizePt);
        Assert.Equal(13.0, StyleResolver.Resolve(book, chapter, page).JournalText!.SizePt);

        Assert.Equal(StyleLevel.Page, StyleResolver.LevelOf(s => s.JournalText?.SizePt, book, chapter, page));
        Assert.Equal(StyleLevel.Chapter, StyleResolver.LevelOf(s => s.JournalText?.SizePt, book, chapter));
        Assert.Equal(StyleLevel.Book, StyleResolver.LevelOf(s => s.JournalText?.SizePt, book));
    }

    [Fact]
    public void ImageBordersFlipGloballyWithOneLeaf()
    {
        var book = NewBook();
        book.Style.ImageBorder = new ImageBorderStyle { Enabled = true };

        var style = StyleResolver.Resolve(book);

        Assert.True(style.ImageBorder!.Enabled);
        Assert.Equal(2.0, style.ImageBorder.WidthPt);   // the rest still comes from the shipped defaults
        Assert.Equal("#FFFFFF", style.ImageBorder.Color);
    }

    [Fact]
    public void Resolving_NeverMutatesTheLevelsItMergesFrom()
    {
        var book = NewBook();
        var chapter = new Chapter { StyleOverride = new Style { CaptionText = new TextStyle { SizePt = 9.5 } } };

        var resolved = StyleResolver.Resolve(book, chapter);
        resolved.CaptionText!.SizePt = 42;
        resolved.Background!.Color = "#FF0000";

        Assert.Equal(9.5, chapter.StyleOverride!.CaptionText!.SizePt);
        Assert.Null(chapter.StyleOverride.Background);
        Assert.Equal(8.5, book.Style.CaptionText!.SizePt);
        Assert.Equal("#000000", book.Style.Background!.Color);
    }

    [Fact]
    public void AnEmptyOverrideIsTheIdentity()
    {
        var book = NewBook();
        var chapter = new Chapter { StyleOverride = new Style() };

        Assert.True(chapter.StyleOverride.IsEmpty);
        Assert.Equal(StyleResolver.Resolve(book), StyleResolver.Resolve(book, chapter));
    }

    private static Book NewBook() => new()
    {
        Id = "bk-test",
        Title = "Our 2024",
        Year = 2024,
        Style = BuiltInStyles.Default,
    };
}
