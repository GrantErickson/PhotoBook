using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.Input;

namespace PhotoBook.App.Services;

/// <summary>
/// One reversible edit (doc 09 §4, kernel §8). Commands are the <b>only</b> way the model changes:
/// every gesture in the editor produces one, runs it on the UI thread through
/// <see cref="UndoStack"/>, and can be taken back with <c>Ctrl+Z</c>.
/// <para>
/// <see cref="Do"/> and <see cref="Undo"/> take no model argument on purpose — the app has a single
/// writer (<see cref="ProjectSession"/>) and a command closes over exactly the objects it edits.
/// <b>Capture enough state</b>: an undo must restore the model exactly, so store the previous value
/// (or a snapshot of the touched pages) rather than trying to recompute it.
/// </para>
/// </summary>
public interface IUndoableCommand
{
    /// <summary>
    /// Human-readable, sentence-case, no trailing period — it is shown as "Undo {Description}".
    /// e.g. <c>"Move photo to slot 3"</c>, <c>"Change template to Four up with journal"</c>.
    /// </summary>
    string Description { get; }

    /// <summary>Applies the edit. Called once by <see cref="UndoStack.Execute(IUndoableCommand)"/> and again on redo.</summary>
    void Do();

    /// <summary>Restores the exact state that existed before <see cref="Do"/> ran.</summary>
    void Undo();

    /// <summary>
    /// Identity for coalescing. Two consecutive commands may merge only when both keys are non-null
    /// and equal, so the key must name the <em>target</em> as well as the kind of edit
    /// (<c>"crop.pan:pg-7:s2"</c>) — coalescing never crosses targets (doc 09 §4). Null (the default)
    /// means this command always gets its own undo entry.
    /// <para>
    /// A key alone coalesces only inside <see cref="UndoStack.CoalesceWindow"/> (500 ms — wheel
    /// ticks, <c>+</c>/<c>-</c> presses). For a continuous drag use the explicit scope
    /// <see cref="UndoStack.BeginGesture"/>, which has no time limit.
    /// </para>
    /// </summary>
    string? CoalesceKey => null;

    /// <summary>
    /// Folds <paramref name="next"/> — an already-executed command against the same target — into
    /// this one, so a whole gesture is a single undo entry. Keep this command's <see cref="Undo"/>
    /// state (the state before the gesture began) and adopt <paramref name="next"/>'s redo state.
    /// Return false to refuse, and the stack pushes a separate entry instead.
    /// </summary>
    bool TryCoalesceWith(IUndoableCommand next) => false;
}

/// <summary>
/// A command expressed as two lambdas, so a simple edit needs no class of its own.
/// <para>
/// Coalescing folds correctly by construction: the merged command keeps the <em>first</em> undo
/// action and the <em>last</em> redo action, which is exactly "one entry for the whole drag".
/// </para>
/// </summary>
/// <example>
/// <code>
/// undo.Execute(new EditCommand(
///     $"Pin page {number}",
///     () => { page.Pinned = true;  session.MarkDirty(); },
///     () => { page.Pinned = false; session.MarkDirty(); }));
/// </code>
/// </example>
public sealed class EditCommand : IUndoableCommand
{
    private Action _redo;
    private Action _undo;
    private string _description;

    /// <param name="description">What the edit is called in the Edit menu and toasts.</param>
    /// <param name="redo">Applies the edit; run immediately by <see cref="UndoStack.Execute(IUndoableCommand)"/>.</param>
    /// <param name="undo">Puts the model back exactly as it was.</param>
    /// <param name="coalesceKey">See <see cref="IUndoableCommand.CoalesceKey"/>.</param>
    public EditCommand(string description, Action redo, Action undo, string? coalesceKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(redo);
        ArgumentNullException.ThrowIfNull(undo);

        _description = description;
        _redo = redo;
        _undo = undo;
        CoalesceKey = coalesceKey;
    }

    /// <inheritdoc/>
    public string Description => _description;

    /// <inheritdoc/>
    public string? CoalesceKey { get; }

    /// <summary>
    /// The common shape: one value moves from <paramref name="before"/> to <paramref name="after"/>.
    /// Ideal for slider and pan drags, which supply a <paramref name="coalesceKey"/> or run inside
    /// <see cref="UndoStack.BeginGesture"/>.
    /// </summary>
    /// <typeparam name="T">The value type being edited — a <c>CropState</c>, a date, a tier.</typeparam>
    /// <param name="description">What the edit is called.</param>
    /// <param name="before">The value to restore on undo.</param>
    /// <param name="after">The value to apply.</param>
    /// <param name="apply">Writes a value into the model (and marks the session dirty).</param>
    /// <param name="coalesceKey">See <see cref="IUndoableCommand.CoalesceKey"/>.</param>
    public static EditCommand ForValue<T>(
        string description, T before, T after, Action<T> apply, string? coalesceKey = null)
    {
        ArgumentNullException.ThrowIfNull(apply);
        return new EditCommand(description, () => apply(after), () => apply(before), coalesceKey);
    }

    /// <inheritdoc/>
    public void Do() => _redo();

    /// <inheritdoc/>
    public void Undo() => _undo();

    /// <inheritdoc/>
    public bool TryCoalesceWith(IUndoableCommand next)
    {
        if (next is not EditCommand other)
        {
            return false;
        }

        // Keep _undo (the state before the gesture started) and adopt the newest redo action.
        _redo = other._redo;
        _description = other._description;
        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => Description;
}

/// <summary>
/// Several commands that must undo together — a template switch that also moves overflow photos to
/// the Unplaced bin, a re-date that vacates a slot, an engine run over a page range (doc 09 §4
/// "Composites"). <see cref="Undo"/> reverses the children in the opposite order to <see cref="Do"/>.
/// </summary>
public sealed class CompositeCommand : IUndoableCommand
{
    private readonly IUndoableCommand[] _children;

    /// <param name="description">The composite's name — "Change template", not the children's names.</param>
    /// <param name="children">The parts, in the order they must be applied.</param>
    public CompositeCommand(string description, IEnumerable<IUndoableCommand> children)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(children);

        Description = description;
        _children = [.. children];
    }

    /// <inheritdoc cref="CompositeCommand(string, IEnumerable{IUndoableCommand})"/>
    public CompositeCommand(string description, params IUndoableCommand[] children)
        : this(description, (IEnumerable<IUndoableCommand>)children)
    {
    }

    /// <inheritdoc/>
    public string Description { get; }

    /// <summary>The parts, in application order.</summary>
    public IReadOnlyList<IUndoableCommand> Children => _children;

    /// <inheritdoc/>
    public void Do()
    {
        foreach (var child in _children)
        {
            child.Do();
        }
    }

    /// <inheritdoc/>
    public void Undo()
    {
        for (var i = _children.Length - 1; i >= 0; i--)
        {
            _children[i].Undo();
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Description} ({_children.Length} steps)";
}

/// <summary>What just happened to the stack, for <see cref="UndoStack.Changed"/> listeners.</summary>
public enum UndoStackChange
{
    /// <summary>A command ran and became a new entry.</summary>
    Executed,

    /// <summary>A command ran and was folded into the entry already on top (one gesture, one entry).</summary>
    Coalesced,

    /// <summary>The top entry was undone.</summary>
    Undone,

    /// <summary>An undone entry was redone.</summary>
    Redone,

    /// <summary>The history was emptied — a different book was opened.</summary>
    Cleared,
}

/// <summary>Payload of <see cref="UndoStack.Changed"/>.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Command">The command involved; null for <see cref="UndoStackChange.Cleared"/>.</param>
public readonly record struct UndoStackChangedEventArgs(UndoStackChange Kind, IUndoableCommand? Command);

/// <summary>
/// The editor's undo history (doc 09 §4, kernel §8): one stack per open book, capacity
/// <see cref="DefaultCapacity"/>, in-memory only, linear (a new edit clears redo).
/// <para>
/// <b>Single writer.</b> Commands run on the UI thread only; background work hands results back
/// through <see cref="JobQueue.PostUi"/> and commits through a command here.
/// </para>
/// <para>
/// <b>Coalescing</b> keeps a continuous gesture to one entry. Prefer the explicit scope — it does
/// not guess from timing:
/// </para>
/// <code>
/// using (undo.BeginGesture($"crop.pan:{page.Id}:{slotId}"))
/// {
///     // every pointer-move executes a command; they all fold into one entry
///     undo.Execute(EditCommand.ForValue("Pan crop", before, after, c => placement.Crop = c));
/// }   // Dispose ends the gesture and seals the entry
/// </code>
/// <para>
/// Without a gesture, a command's own <see cref="IUndoableCommand.CoalesceKey"/> still merges
/// repeats within <see cref="CoalesceWindow"/> — the 500 ms rule for wheel ticks and key repeats.
/// </para>
/// </summary>
public sealed class UndoStack : INotifyPropertyChanged
{
    /// <summary>Entries kept before the oldest is dropped (doc 09 §4).</summary>
    public const int DefaultCapacity = 200;

    private readonly LinkedList<Entry> _undo = new();
    private readonly List<IUndoableCommand> _redo = [];
    private readonly int _capacity;

    private string? _gestureKey;
    private int _gestureDepth;
    private Batch? _batch;
    private bool _applying;

    /// <param name="capacity">History depth; defaults to the spec's 200.</param>
    public UndoStack(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;

        UndoCommand = new RelayCommand(() => Undo(), () => CanUndo);
        RedoCommand = new RelayCommand(() => Redo(), () => CanRedo);
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised after every mutation, on the UI thread. Wire this to
    /// <see cref="ProjectSession.MarkDirty"/> and to whatever needs to re-render.
    /// </summary>
    public event EventHandler<UndoStackChangedEventArgs>? Changed;

    /// <summary>Bindable command for the toolbar button and <c>Ctrl+Z</c>.</summary>
    public IRelayCommand UndoCommand { get; }

    /// <summary>Bindable command for the toolbar button, <c>Ctrl+Y</c> and <c>Ctrl+Shift+Z</c>.</summary>
    public IRelayCommand RedoCommand { get; }

    /// <summary>
    /// How long a <see cref="IUndoableCommand.CoalesceKey"/> keeps merging outside an explicit
    /// gesture. 500 ms per doc 09 §4.
    /// </summary>
    public TimeSpan CoalesceWindow { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>True when there is something to take back.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>True when something has been undone and not superseded by a new edit.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Description of the entry <see cref="Undo"/> would reverse, or null.</summary>
    public string? UndoDescription => _undo.Last?.Value.Command.Description;

    /// <summary>Description of the entry <see cref="Redo"/> would reapply, or null.</summary>
    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    /// <summary>Menu-ready label, e.g. "Undo Move photo to slot 3".</summary>
    public string UndoLabel => CanUndo ? $"Undo {UndoDescription}" : "Undo";

    /// <summary>Menu-ready label, e.g. "Redo Move photo to slot 3".</summary>
    public string RedoLabel => CanRedo ? $"Redo {RedoDescription}" : "Redo";

    /// <summary>Entries currently on the undo side.</summary>
    public int Count => _undo.Count;

    /// <summary>Entries currently on the redo side.</summary>
    public int RedoCount => _redo.Count;

    /// <summary>True between <see cref="BeginGesture"/> and the end of that scope.</summary>
    public bool IsInGesture => _gestureKey is not null;

    /// <summary>The undo entries, oldest first — for a history panel.</summary>
    public IEnumerable<IUndoableCommand> Entries => _undo.Select(e => e.Command);

    // ------------------------------------------------------------------ execute

    /// <summary>
    /// Runs <paramref name="command"/> and records it, clearing redo. This is how every model
    /// mutation in the editor happens.
    /// </summary>
    public void Execute(IUndoableCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureUiThread();
        EnsureNotApplying();

        command.Do();
        Record(command);
    }

    /// <summary>Runs a lambda pair as one undoable edit.</summary>
    /// <param name="description">What the edit is called.</param>
    /// <param name="redo">Applies the edit.</param>
    /// <param name="undo">Restores the previous state exactly.</param>
    /// <param name="coalesceKey">See <see cref="IUndoableCommand.CoalesceKey"/>.</param>
    public void Execute(string description, Action redo, Action undo, string? coalesceKey = null) =>
        Execute(new EditCommand(description, redo, undo, coalesceKey));

    /// <summary>Runs a single-value edit as one undoable command. See <see cref="EditCommand.ForValue"/>.</summary>
    /// <typeparam name="T">The value type being edited.</typeparam>
    /// <param name="description">What the edit is called.</param>
    /// <param name="before">The value to restore on undo.</param>
    /// <param name="after">The value to apply.</param>
    /// <param name="apply">Writes a value into the model.</param>
    /// <param name="coalesceKey">See <see cref="IUndoableCommand.CoalesceKey"/>.</param>
    public void ExecuteValue<T>(
        string description, T before, T after, Action<T> apply, string? coalesceKey = null) =>
        Execute(EditCommand.ForValue(description, before, after, apply, coalesceKey));

    /// <summary>
    /// Records a command whose effect is <b>already applied</b> — for the rare case where the edit
    /// happened as a side effect (an engine run committed on the job queue) and only needs to become
    /// undoable. <see cref="IUndoableCommand.Do"/> is not called now, but is on redo.
    /// </summary>
    public void Push(IUndoableCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureUiThread();
        EnsureNotApplying();
        Record(command);
    }

    // ------------------------------------------------------------------ gestures

    /// <summary>
    /// Opens an explicit coalescing scope: every command executed until the returned scope is
    /// disposed folds into <b>one</b> undo entry. Use it for the whole life of a continuous gesture —
    /// pointer-down to pointer-up on a crop pan, thumb-press to thumb-release on a slider.
    /// <para>
    /// The key must identify the target so two gestures on different slots never merge; nested calls
    /// with the same key are reference-counted, and a different key is rejected.
    /// </para>
    /// </summary>
    /// <param name="key">Gesture identity, e.g. <c>$"crop.pan:{page.Id}:{slotId}"</c>.</param>
    /// <returns>A scope that calls <see cref="EndGesture"/> when disposed.</returns>
    public IDisposable BeginGesture(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        EnsureUiThread();

        if (_gestureKey is null)
        {
            _gestureKey = key;
            _gestureDepth = 1;
        }
        else if (string.Equals(_gestureKey, key, StringComparison.Ordinal))
        {
            _gestureDepth++;
        }
        else
        {
            throw new InvalidOperationException(
                $"A '{_gestureKey}' gesture is already open; '{key}' would silently merge two targets. " +
                "End the first gesture before starting another.");
        }

        RaisePropertyChanged(nameof(IsInGesture));
        return new Scope(this, isGesture: true);
    }

    /// <summary>
    /// Closes the innermost <see cref="BeginGesture"/> scope and seals its entry, so the next edit
    /// starts a fresh one. Safe to call when no gesture is open.
    /// </summary>
    public void EndGesture()
    {
        EnsureUiThread();
        if (_gestureKey is null)
        {
            return;
        }

        if (--_gestureDepth > 0)
        {
            return;
        }

        // Sealing the top entry is what guarantees "one gesture, one entry": a later command with
        // the same key can no longer merge into it, whatever the clock says.
        if (_undo.Last is { } node)
        {
            node.Value.Key = null;
        }

        _gestureKey = null;
        RaisePropertyChanged(nameof(IsInGesture));
    }

    /// <summary>
    /// Collects everything executed inside the scope into one <see cref="CompositeCommand"/> — the
    /// doc 09 §4 composite: a template switch, a re-date, an engine run. Children are applied as they
    /// are executed; the single entry appears when the scope closes.
    /// </summary>
    /// <param name="description">The composite's name in the Edit menu.</param>
    public IDisposable BeginBatch(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        EnsureUiThread();

        _batch = new Batch(description, _batch);
        return new Scope(this, isGesture: false);
    }

    private void EndBatch()
    {
        var batch = _batch;
        if (batch is null)
        {
            return;
        }

        _batch = batch.Parent;

        if (batch.Commands.Count == 0)
        {
            return;
        }

        // A batch of one that already carries the batch's name needs no wrapper.
        var single = batch.Commands.Count == 1 &&
                     string.Equals(batch.Commands[0].Description, batch.Description, StringComparison.Ordinal);

        Record(single ? batch.Commands[0] : new CompositeCommand(batch.Description, batch.Commands));
    }

    // ------------------------------------------------------------------ undo / redo

    /// <summary>Reverses the newest entry. Returns false when there is nothing to undo.</summary>
    public bool Undo()
    {
        EnsureUiThread();
        EnsureNotApplying();

        var node = _undo.Last;
        if (node is null)
        {
            return false;
        }

        var command = node.Value.Command;
        _undo.RemoveLast();

        _applying = true;
        try
        {
            command.Undo();
        }
        finally
        {
            _applying = false;
        }

        _redo.Add(command);
        _gestureKey = null;
        _gestureDepth = 0;
        Raise(UndoStackChange.Undone, command);
        return true;
    }

    /// <summary>Reapplies the newest undone entry. Returns false when there is nothing to redo.</summary>
    public bool Redo()
    {
        EnsureUiThread();
        EnsureNotApplying();

        if (_redo.Count == 0)
        {
            return false;
        }

        var command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        _applying = true;
        try
        {
            command.Do();
        }
        finally
        {
            _applying = false;
        }

        // Sealed (Key = null): a redone entry never absorbs the next edit.
        _undo.AddLast(new Entry(command, null, Stopwatch.GetTimestamp()));
        Trim();
        Raise(UndoStackChange.Redone, command);
        return true;
    }

    /// <summary>Empties the history — call when a different book is opened (one stack per book).</summary>
    public void Clear()
    {
        EnsureUiThread();

        var had = _undo.Count > 0 || _redo.Count > 0;
        _undo.Clear();
        _redo.Clear();
        _gestureKey = null;
        _gestureDepth = 0;
        _batch = null;

        if (had)
        {
            Raise(UndoStackChange.Cleared, null);
        }
        else
        {
            RaiseState();
        }
    }

    // ------------------------------------------------------------------ internals

    private void Record(IUndoableCommand command)
    {
        if (_batch is { } batch)
        {
            batch.Commands.Add(command);
            return;
        }

        _redo.Clear();

        var key = _gestureKey ?? command.CoalesceKey;
        var now = Stopwatch.GetTimestamp();

        if (key is not null && _undo.Last is { } node && node.Value.Key is { } topKey &&
            string.Equals(topKey, key, StringComparison.Ordinal))
        {
            // Inside a gesture the scope is the boundary, so no clock is consulted; outside it, a
            // key only merges repeats of the same edit within CoalesceWindow (doc 09 §4).
            var withinWindow = _gestureKey is not null ||
                               Stopwatch.GetElapsedTime(node.Value.At, now) <= CoalesceWindow;

            if (withinWindow && node.Value.Command.TryCoalesceWith(command))
            {
                node.Value.At = now;
                Raise(UndoStackChange.Coalesced, node.Value.Command);
                return;
            }
        }

        _undo.AddLast(new Entry(command, key, now));
        Trim();
        Raise(UndoStackChange.Executed, command);
    }

    private void Trim()
    {
        while (_undo.Count > _capacity)
        {
            _undo.RemoveFirst();
        }
    }

    private void Raise(UndoStackChange kind, IUndoableCommand? command)
    {
        RaiseState();
        Changed?.Invoke(this, new UndoStackChangedEventArgs(kind, command));
    }

    private void RaiseState()
    {
        RaisePropertyChanged(nameof(CanUndo));
        RaisePropertyChanged(nameof(CanRedo));
        RaisePropertyChanged(nameof(UndoDescription));
        RaisePropertyChanged(nameof(RedoDescription));
        RaisePropertyChanged(nameof(UndoLabel));
        RaisePropertyChanged(nameof(RedoLabel));
        RaisePropertyChanged(nameof(Count));
        RaisePropertyChanged(nameof(RedoCount));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void RaisePropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void EnsureNotApplying()
    {
        if (_applying)
        {
            throw new InvalidOperationException(
                "A command cannot execute another command while undo or redo is running. " +
                "Group related edits with CompositeCommand or UndoStack.BeginBatch instead.");
        }
    }

    private static void EnsureUiThread()
    {
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "The model has a single writer: undoable commands must run on the UI thread " +
                "(kernel §8). Marshal with JobQueue.PostUi.");
        }
    }

    private sealed class Entry(IUndoableCommand command, string? key, long at)
    {
        public IUndoableCommand Command { get; } = command;

        /// <summary>Coalescing identity of this entry; nulled once sealed.</summary>
        public string? Key { get; set; } = key;

        /// <summary>Timestamp of the last fold, for <see cref="CoalesceWindow"/>.</summary>
        public long At { get; set; } = at;
    }

    private sealed class Batch(string description, Batch? parent)
    {
        public string Description { get; } = description;

        public Batch? Parent { get; } = parent;

        public List<IUndoableCommand> Commands { get; } = [];
    }

    private sealed class Scope(UndoStack owner, bool isGesture) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            if (isGesture)
            {
                owner.EndGesture();
            }
            else
            {
                owner.EndBatch();
            }
        }
    }
}
