using PhotoBook.Imaging.Auto;

namespace PhotoBook.Tests;

/// <summary>
/// The arithmetic behind the confirmation dialog. The counts are the whole point of putting a dialog
/// in front of a book-wide change — a wrong number is how someone agrees to something they did not
/// mean — so they are worth asserting away from the modal window that shows them.
/// </summary>
public sealed class AutoAdjustPlanTests
{
    [Fact]
    public void ScopeCountsHandEditedPhotosOnlyWhenTheyAreOptedIn()
    {
        var plan = new AutoAdjustPlan(Untouched: 40, Automatic: 30, Manual: 12, UpToDate: 25);

        Assert.Equal(70, plan.Scope(includeManual: false));
        Assert.Equal(82, plan.Scope(includeManual: true));
    }

    [Fact]
    public void WorkExcludesThePhotosThatAreAlreadyCurrent()
    {
        // The second click of the button should cost almost nothing, and the dialog should say so
        // rather than threatening a decode of the whole book again.
        var plan = new AutoAdjustPlan(Untouched: 0, Automatic: 300, Manual: 5, UpToDate: 300);

        Assert.Equal(300, plan.Scope(includeManual: false));
        Assert.Equal(0, plan.Work(includeManual: false));
    }

    [Fact]
    public void WorkNeverGoesNegativeWhenUpToDateOutrunsScope()
    {
        // UpToDate only ever counts automatic photos, but the record is a plain value type and
        // nothing stops a caller constructing an inconsistent one. A negative "photos to process"
        // would divide progress by a negative total.
        var plan = new AutoAdjustPlan(Untouched: 0, Automatic: 2, Manual: 0, UpToDate: 9);

        Assert.Equal(0, plan.Work(includeManual: false));
    }

    [Fact]
    public void AnEmptyBookAsksForNothing()
    {
        var plan = default(AutoAdjustPlan);

        Assert.Equal(0, plan.Scope(includeManual: true));
        Assert.Equal(0, plan.Work(includeManual: true));
    }
}
