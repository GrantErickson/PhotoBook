using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PhotoBook.App.ViewModels;

namespace PhotoBook.App.Views;

/// <summary>
/// The open book: chapter rail, Photos grid, and the Pages editor.
///
/// <para>
/// The only behaviour here is doc 09 §5's keyboard map. It is dispatched from <em>one</em> place, the
/// window, because a shortcut whose reach depends on where the user last clicked is not a shortcut:
/// the page keys used to live on <see cref="Pages.PageEditorView"/> as an element handler, so
/// <c>Page Down</c> worked after clicking the canvas and paged a scroll viewer after clicking the
/// filmstrip. The router below decides <em>which</em> surface a key belongs to from the app's own
/// state — tab, mode, selection — and hands it there. The surfaces still own what the keys mean.
/// </para>
///
/// <para>
/// The reference the user reads lives in <see cref="EditorShortcuts"/>, beside this. Both are meant to
/// be edited together.
/// </para>
/// </summary>
public partial class BookView : UserControl
{
    private Window? _window;

    /// <summary>Creates the view.</summary>
    public BookView()
    {
        InitializeComponent();

        // The shortcuts hang off the *window*, not off this control: a tunnelling handler here only
        // ever fires once keyboard focus has already landed inside the workspace, so `T` did nothing
        // until the user had clicked something first.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private BookViewModel? Model => DataContext as BookViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            return;
        }

        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.PreviewKeyDown += OnShortcutKey;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.PreviewKeyDown -= OnShortcutKey;
            _window = null;
        }
    }

    private void OnQuickPreviewClicked(object sender, MouseButtonEventArgs e) =>
        Model?.CloseQuickPreviewCommand.Execute(null);

    // ============================================================ the router

    private void OnShortcutKey(object sender, KeyEventArgs e)
    {
        // A collapsed book view is the dashboard's turn: its keys must not be stolen from it.
        if (!IsVisible || Model is not { } model)
        {
            return;
        }

        // A text field owns every key it can use. This is the whole of "a text box never swallows a
        // shortcut it should not" — and its mirror: `S`, `D`, `E`, `Del` and `Space` are letters
        // before they are commands, so while a caret is blinking they are only letters. The one
        // exception is the window's own Ctrl chords (Ctrl+S, Ctrl+Z), which are InputBindings and
        // deliberately still reach the shell from inside a field.
        if (IsTextEntry(e.OriginalSource))
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            return;
        }

        var ctrl = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        e.Handled = Route(model, e, ctrl, shift);
    }

    private bool Route(BookViewModel model, KeyEventArgs e, bool ctrl, bool shift)
    {
        // ---------------------------------------------------------------- anywhere
        if (e.Key is Key.F1 || (e.Key is Key.OemQuestion && shift))
        {
            model.ShowShortcutsCommand.Execute(null);
            return true;
        }

        if (ctrl)
        {
            return e.Key switch
            {
                // Ctrl+Tab flips the two halves of the month workspace, in both directions.
                Key.Tab => Run(model.ToggleTabCommand),

                // Viewport zoom. Ctrl+wheel is the canvas's own; these are its keyboard twins.
                Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract when model.IsPagesTab =>
                    PageSurface.HandleShortcut(e),

                // Ctrl+Z / Ctrl+Y / Ctrl+S are the window's InputBindings — leave them alone.
                _ => false,
            };
        }

        switch (e.Key)
        {
            // Esc is a stack, not a key: it undoes the most recent thing that changed the app's mode.
            case Key.Escape:
                return HandleEscape(model, e);

            // ---- photo keys: live in either tab while a photo is selected (doc 09 §5)
            case Key.D when model.SelectedPhoto is not null:
                return Run(model.Inspector.ChangeDateCommand);

            case Key.F when model.SelectedPhoto is not null:
                return Run(model.Inspector.EditFocusCommand);

            case Key.OemOpenBrackets when model.SelectedPhoto is not null:
                return Run(model.DemoteCommand);

            case Key.OemCloseBrackets when model.SelectedPhoto is not null:
                return Run(model.PromoteCommand);

            case Key.E:
                return Exclude(model);

            // ---- Photos tab
            case Key.Space when !model.IsPagesTab && model.SelectedPhoto is not null && !IsButton(e.OriginalSource):
                return Run(model.ToggleQuickPreviewCommand);

            case Key.Enter when !model.IsPagesTab && model.SelectedPhoto is not null && !IsButton(e.OriginalSource):
                return FocusInspector(model);
        }

        return model.IsPagesTab && RoutePagesTab(model, e);
    }

    private bool RoutePagesTab(BookViewModel model, KeyEventArgs e)
    {
        var editor = model.PageEditor;
        var inOverride = model.PageOverride.IsActive;

        switch (e.Key)
        {
            // ---- panels
            case Key.T:
                return Run(model.ToggleTemplatePickerCommand);

            case Key.B:
                return Run(model.ToggleBinsCommand);

            case Key.L:
                return Run(model.PageOverride.ToggleCommand);

            case Key.G:
                return Run(model.PageEditor.ToggleGuidesCommand);

            // ---- navigation and view: the surface's job, wherever the focus happens to be
            case Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.F11 or Key.S:
                return PageSurface.HandleShortcut(e);

            // Tab only becomes "next slot" once a slot is selected; otherwise it stays WPF's focus
            // traversal, which is how the toolbar is reachable without a mouse.
            case Key.Tab when !inOverride && editor.SelectedSlotId is not null:
                return PageSurface.HandleShortcut(e);

            case Key.Enter when !inOverride && editor.SelectedSlotId is not null && !IsButton(e.OriginalSource):
                return PageSurface.HandleShortcut(e);

            // Del: the selected slot, else the selected container in layout mode, else the bin
            // selection (R17 — "remove from bin" is Exclude).
            case Key.Delete when inOverride:
                return Run(model.PageOverride.DeleteSelectedCommand);

            case Key.Delete when editor.SelectedSlotId is not null:
                return PageSurface.HandleShortcut(e);

            case Key.Delete:
                return ExcludeFromBin(model);

            // ---- crop mode (doc 09 §3.3)
            case Key.D0 or Key.NumPad0 or Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract
                or Key.Left or Key.Right or Key.Up or Key.Down when editor.IsCropMode:
                return PageSurface.HandleShortcut(e);

            default:
                return false;
        }
    }

    /// <summary>
    /// Esc backs out of exactly one thing, most recent first, so it never destroys two states at
    /// once — leaving crop mode must not also close the drawer the user was reading.
    /// </summary>
    private bool HandleEscape(BookViewModel model, KeyEventArgs e)
    {
        if (model.IsQuickPreviewOpen)
        {
            return Run(model.CloseQuickPreviewCommand);
        }

        if (model.IsPagesTab && model.PageEditor.IsCropMode)
        {
            return PageSurface.HandleShortcut(e);
        }

        if (model.PageOverride.IsActive)
        {
            // A geometry drag in progress is the override surface's own to cancel, and it has focus
            // while the mouse is captured; only the mode itself is the shell's to close.
            if (Keyboard.FocusedElement is Pages.LayoutOverrideSurface)
            {
                return false;
            }

            model.PageOverride.Exit();
            return true;
        }

        if (model.IsTemplatePickerOpen || model.IsStylePanelOpen)
        {
            model.IsTemplatePickerOpen = false;
            model.IsStylePanelOpen = false;
            return true;
        }

        if (model.IsPagesTab && model.PageEditor.SelectedSlotId is not null)
        {
            return PageSurface.HandleShortcut(e);
        }

        if (model.Bins.SelectedItem is not null)
        {
            model.Bins.SelectedItem = null;
            return true;
        }

        return false;
    }

    /// <summary>
    /// <c>E</c>: exclude from the book. In the Pages tab the bin selection is what the user is looking
    /// at, so it wins; in the Photos tab it is the grid selection (R17, doc 09 §3.9).
    /// </summary>
    private static bool Exclude(BookViewModel model)
    {
        if (model.IsPagesTab)
        {
            return ExcludeFromBin(model);
        }

        return model.SelectedPhoto is not null && Run(model.ToggleExcludeCommand);
    }

    private static bool ExcludeFromBin(BookViewModel model) =>
        model.Bins.SelectedItem is not null && Run(model.Bins.ExcludeCommand);

    /// <summary>
    /// <c>Enter</c> on the Photos tab. The inspector is a rail rather than a window here, so "open
    /// the inspector" means put the keyboard in it — the sections, the date field and the sliders are
    /// then all reachable with Tab.
    /// </summary>
    private bool FocusInspector(BookViewModel model)
    {
        Inspector.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        model.StatusMessage = model.SelectedPhoto is { } photo
            ? $"Inspector: {photo.FileName}"
            : model.StatusMessage;
        return true;
    }

    /// <summary>Runs a command if it is available, and reports whether the key was consumed.</summary>
    private static bool Run(ICommand command)
    {
        if (!command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    /// <summary>True when the key belongs to a text field rather than to the editor.</summary>
    private static bool IsTextEntry(object? source) =>
        source is TextBoxBase or PasswordBox ||
        (source is ComboBox { IsEditable: true, IsReadOnly: false });

    /// <summary>
    /// True for a control that already means something by <c>Space</c> and <c>Enter</c>. Pressing a
    /// focused button must press it, not open a photo preview behind it.
    /// </summary>
    private static bool IsButton(object? source) => source is ButtonBase;
}
