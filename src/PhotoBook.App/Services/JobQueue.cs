using System.Collections.ObjectModel;
using System.Threading.Channels;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoBook.App.Services;

/// <summary>One unit of background work, surfaced to the shell so progress is always visible.</summary>
public sealed partial class Job : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    public CancellationTokenSource Cancellation { get; } = new();

    public void Cancel() => Cancellation.Cancel();
}

/// <summary>
/// The single background worker for heavy operations (import, analysis, layout, export), per
/// kernel §8. Nothing expensive runs on the dispatcher; results marshal back through
/// <see cref="Dispatcher"/>. Work is serialized so two imports cannot race the same catalog.
/// </summary>
public sealed class JobQueue : IAsyncDisposable, System.ComponentModel.INotifyPropertyChanged
{
    private readonly Channel<Func<Job, Task>> _channel =
        Channel.CreateUnbounded<Func<Job, Task>>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Task _pump;
    private readonly Job _placeholder = new();

    public JobQueue()
    {
        _pump = Task.Run(PumpAsync);
        Jobs.CollectionChanged += (_, _) => PropertyChanged?.Invoke(this, new(nameof(HasJobs)));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Jobs currently running or queued. Observed by the shell's progress strip.</summary>
    public ObservableCollection<Job> Jobs { get; } = [];

    /// <summary>True while any work is queued, so the shell only shows the strip when it means something.</summary>
    public bool HasJobs => Jobs.Count > 0;

    /// <summary>Raised when a job throws, so the shell can surface it rather than swallowing it.</summary>
    public event Action<string, Exception>? JobFailed;

    /// <summary>Queues work and returns when that work has finished.</summary>
    public Task RunAsync(string title, Func<Job, Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _channel.Writer.TryWrite(async job =>
        {
            job.Title = title;
            try
            {
                await work(job).ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled();
            }
            catch (Exception ex)
            {
                JobFailed?.Invoke(title, ex);
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }

    private async Task PumpAsync()
    {
        await foreach (var work in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var job = new Job();
            await OnUiAsync(() => Jobs.Add(job)).ConfigureAwait(false);
            try
            {
                await work(job).ConfigureAwait(false);
            }
            catch
            {
                // RunAsync already routed this to JobFailed and the awaiting caller.
            }
            finally
            {
                job.Cancellation.Dispose();
                await OnUiAsync(() => Jobs.Remove(job)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Marshals an action onto the UI thread, awaiting completion.</summary>
    public static Task OnUiAsync(Action action)
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return app.Dispatcher.InvokeAsync(action).Task;
    }

    /// <summary>Fire-and-forget marshal for progress updates, which must never block the worker.</summary>
    public static void PostUi(Action action)
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            app.Dispatcher.BeginInvoke(action);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch
        {
            // Shutdown is best-effort.
        }

        _placeholder.Cancellation.Dispose();
    }
}
