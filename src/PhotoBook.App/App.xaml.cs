using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using PhotoBook.App.Services;
using PhotoBook.App.ViewModels;

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
        services.AddSingleton<BookViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();

        _services = services.BuildServiceProvider();

        // An unhandled dispatcher exception would otherwise close the app with no explanation.
        DispatcherUnhandledException += OnDispatcherException;

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
            if (session is { IsOpen: true, IsDirty: true })
            {
                try
                {
                    await session.SaveAsync();
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
