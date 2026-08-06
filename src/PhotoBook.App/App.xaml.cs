using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using PhotoBook.App.Services;
using PhotoBook.App.ViewModels;
using PhotoBook.App.ViewModels.Export;
using PhotoBook.App.ViewModels.Journal;
using PhotoBook.App.ViewModels.Pages;
using PhotoBook.App.ViewModels.Photos;

namespace PhotoBook.App;

/// <summary>Composition root. Everything is resolved from one container (ADR-0009).</summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddSingleton<ThumbnailProvider>();
        services.AddSingleton<JobQueue>();
        services.AddSingleton<ProjectSession>();

        // One undo stack per process, cleared whenever a book opens or closes (doc 09 §4).
        services.AddSingleton<UndoStack>();
        services.AddSingleton(_ => new EditorSettingsService());

        // Photos tab: the inspector and everything it edits.
        services.AddSingleton<PhotoEditor>();
        services.AddSingleton(sp => new PhotoPreviewService(sp.GetRequiredService<ProjectSession>()));
        services.AddSingleton<AutoAdjustRunner>();
        services.AddSingleton<AdjustmentsViewModel>();
        services.AddSingleton<FocusRegionEditorViewModel>();
        services.AddSingleton<PhotoReorderController>();
        services.AddSingleton<PhotoInspectorViewModel>();

        // Pages tab: canvas, bins, template gallery, auto-layout commands, override mode.
        // The page editor gets its OWN adjustments panel rather than sharing the inspector's: the
        // two are attached to different photos at the same time (the grid's selection and the
        // selected slot's), and one instance cannot be pointed at both.
        services.AddSingleton(sp => new PageEditorViewModel(
            sp.GetRequiredService<ProjectSession>(),
            sp.GetRequiredService<UndoStack>(),
            sp.GetRequiredService<ThumbnailProvider>(),
            sp.GetRequiredService<EditorSettingsService>(),
            new AdjustmentsViewModel(
                sp.GetRequiredService<PhotoEditor>(),
                sp.GetRequiredService<PhotoPreviewService>(),
                sp.GetRequiredService<AutoAdjustRunner>()),
            sp.GetRequiredService<PhotoEditor>()));
        services.AddSingleton<BinsViewModel>();
        services.AddSingleton<TemplatePickerViewModel>();
        services.AddSingleton<LayoutCommandsViewModel>();
        services.AddSingleton<PageOverrideViewModel>();

        // Book-wide panels.
        services.AddSingleton<StyleViewModel>();
        services.AddSingleton<ExportViewModel>();
        services.AddSingleton<JournalReviewViewModel>();

        services.AddSingleton<BookViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();

        _services = services.BuildServiceProvider();

        // An unhandled dispatcher exception would otherwise close the app with no explanation.
        DispatcherUnhandledException += OnDispatcherException;

        // Log off / shut down / restart gives no chance to await. FlushOnShutdown blocks for a bounded
        // time and is safe from the UI thread — nothing in the save path marshals back to the
        // dispatcher (doc 04 §6).
        SessionEnding += (_, _) => _services?.GetService<ProjectSession>()?.FlushOnShutdown();

        try
        {
            var window = _services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();

            // `PhotoBook.App --open <folder>` opens a book straight away, which is what a
            // file-association or "open recent" shortcut needs.
            var open = Array.IndexOf(e.Args, "--open");
            if (open >= 0 && open + 1 < e.Args.Length)
            {
                var shell = _services.GetRequiredService<ShellViewModel>();
                var folder = e.Args[open + 1];
                _ = window.Dispatcher.InvokeAsync(async () => await shell.OpenPathAsync(folder));
            }
        }
        catch (Exception ex)
        {
            // Also write it down: a modal MessageBox is invisible to automation and to anyone
            // running the app headlessly, and "it just doesn't start" is the worst bug report there
            // is. The file is the first thing to read when the window never appears.
            TryWriteStartupCrash(ex);

            // A failure here leaves no window to show the error in, so say so plainly and stop
            // rather than sitting invisible in the process list.
            MessageBox.Show(
                $"PhotoBook could not start.\n\n{ex}",
                "PhotoBook",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Records a startup failure beside the user's settings, best effort.</summary>
    private static void TryWriteStartupCrash(Exception ex)
    {
        try
        {
            var folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoBook");
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(folder, "startup-error.txt"),
                DateTime.Now.ToString("u", System.Globalization.CultureInfo.InvariantCulture)
                    + Environment.NewLine + ex);
        }
        catch
        {
            // Nothing useful to do if even this fails.
        }
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        if (_services?.GetService<ShellViewModel>() is { } shell)
        {
            shell.ErrorMessage = e.Exception.Message;
        }
        else
        {
            MessageBox.Show(e.Exception.Message, "PhotoBook", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_services is not null)
        {
            var session = _services.GetService<ProjectSession>();
            if (session is not null)
            {
                try
                {
                    // Waits for a save already in flight, then writes what is left, then closes.
                    // ShutdownAsync never throws for a failed write; the try is for the unexpected.
                    await session.ShutdownAsync();
                }
                catch
                {
                    // Shutdown must not hang on a failed save.
                }
            }

            if (_services.GetService<JobQueue>() is { } jobs)
            {
                await jobs.DisposeAsync();
            }

            session?.Dispose();
            await _services.DisposeAsync();
        }

        base.OnExit(e);
    }
}
