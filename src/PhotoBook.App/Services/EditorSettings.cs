using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoBook.App.Services;

/// <summary>Where the bin panel is docked (doc 09 §3.5, R13). A per-user preference, never book content.</summary>
public enum BinDock
{
    /// <summary>The default: a horizontal filmstrip across the bottom of the Pages tab, 148 px tall.</summary>
    Bottom,

    /// <summary>A vertical list on the left of the canvas, 200 px wide.</summary>
    Left,

    /// <summary>A vertical list on the right of the canvas, 200 px wide.</summary>
    Right,
}

/// <summary>Which bin the panel is showing (doc 09 §3.5, plus the Outside-book tray of §2.1).</summary>
public enum BinTab
{
    /// <summary>Photos of this month that are on no page (R10).</summary>
    Unplaced,

    /// <summary>Photos placed on later pages of this chapter, so they can be pulled forward (R13).</summary>
    Upcoming,

    /// <summary>Photos whose date puts them outside the book's year (R6) — nothing silently disappears.</summary>
    Outside,
}

/// <summary>Which section of the Photos-tab inspector is open (doc 09 §2). The switcher's memory.</summary>
public enum InspectorSection
{
    /// <summary>Date, quality Tier and exclusion — what the photo <i>is</i> (§2.1).</summary>
    Info,

    /// <summary>Exposure, colour and orientation — the image corrections of R11 (§2.4).</summary>
    Adjust,

    /// <summary>The Focus Regions smart crop must keep in frame (R25, §2.2).</summary>
    Focus,
}

/// <summary>
/// The per-user editor preferences that survive a restart: bin dock side, bin size, which bin tab
/// was open, the template gallery's filter, the page guides toggle and the inspector section.
/// Doc 09 §3.5 is explicit that these are
/// <b>user settings, not book content</b>, so they live beside the OneDrive config in
/// <c>%LOCALAPPDATA%\PhotoBook</c> and never touch the project folder's JSON.
/// </summary>
public sealed partial class EditorSettings : ObservableObject
{
    /// <summary>Schema version of <c>editor-settings.json</c>; currently 1.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Smallest bin height that still shows a thumbnail row.</summary>
    public const double MinBinBottomHeight = 120;

    /// <summary>Largest bin height before the canvas becomes unusable.</summary>
    public const double MaxBinBottomHeight = 420;

    /// <summary>Smallest useful side-dock width.</summary>
    public const double MinBinSideWidth = 148;

    /// <summary>Largest side-dock width before the canvas becomes unusable.</summary>
    public const double MaxBinSideWidth = 460;

    /// <summary>Which edge the bin panel is docked to (R13).</summary>
    [ObservableProperty]
    private BinDock _binDock = BinDock.Bottom;

    /// <summary>Whether the bin panel is shown at all (<c>B</c> toggles it, doc 09 §5).</summary>
    [ObservableProperty]
    private bool _binVisible = true;

    /// <summary>Height of the bottom-docked filmstrip, in device-independent pixels.</summary>
    [ObservableProperty]
    private double _binBottomHeight = 160;

    /// <summary>Width of a side-docked bin, in device-independent pixels.</summary>
    [ObservableProperty]
    private double _binSideWidth = 200;

    /// <summary>The bin tab that was open when the app last closed.</summary>
    [ObservableProperty]
    private BinTab _binTab = BinTab.Unplaced;

    /// <summary>True when the template gallery is showing the whole library rather than the suggestions.</summary>
    [ObservableProperty]
    private bool _templateGalleryShowAll;

    /// <summary>
    /// Whether the Pages canvas draws the bleed/trim/safe guides (<c>G</c>, doc 09 §5). Off by
    /// default: the page's own edge is always drawn, and the guides are the precision layer over it.
    /// </summary>
    [ObservableProperty]
    private bool _pageGuidesVisible;

    /// <summary>The Photos-tab inspector section that was open when the app last closed.</summary>
    [ObservableProperty]
    private InspectorSection _inspectorSection = InspectorSection.Info;

    /// <summary>Brings loaded values back into their legal ranges, so a hand-edited file cannot wedge the UI.</summary>
    public void Normalize()
    {
        BinBottomHeight = Math.Clamp(
            double.IsFinite(BinBottomHeight) ? BinBottomHeight : 160, MinBinBottomHeight, MaxBinBottomHeight);
        BinSideWidth = Math.Clamp(
            double.IsFinite(BinSideWidth) ? BinSideWidth : 200, MinBinSideWidth, MaxBinSideWidth);

        if (!Enum.IsDefined(BinDock))
        {
            BinDock = BinDock.Bottom;
        }

        if (!Enum.IsDefined(BinTab))
        {
            BinTab = BinTab.Unplaced;
        }

        if (!Enum.IsDefined(InspectorSection))
        {
            InspectorSection = InspectorSection.Info;
        }
    }
}

/// <summary>
/// Loads and saves <see cref="EditorSettings"/> as one small JSON file under
/// <c>%LOCALAPPDATA%\PhotoBook</c>. Writes are debounced and atomic (temp file + replace), so
/// dragging the bin splitter does not hammer the disk and a crash mid-write cannot leave a
/// half-written file behind.
/// <para>
/// This is deliberately <b>not</b> a second project format: nothing here belongs to a book, and a
/// project folder stays portable without it.
/// </para>
/// </summary>
public sealed class EditorSettingsService : IDisposable
{
    /// <summary>File name inside the PhotoBook local-app-data folder.</summary>
    public const string FileName = "editor-settings.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly Timer _debounce;
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <param name="path">
    /// Where to keep the file; defaults to <see cref="DefaultPath"/>. Tests pass a temp path.
    /// </param>
    public EditorSettingsService(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
        Settings = Load(_path);
        _debounce = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        Settings.PropertyChanged += (_, _) => Schedule();
    }

    /// <summary><c>%LOCALAPPDATA%\PhotoBook\editor-settings.json</c>.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoBook",
        FileName);

    /// <summary>The live settings object. Bind to it; every change schedules a save.</summary>
    public EditorSettings Settings { get; }

    /// <summary>How long a change waits before it is written, so a drag produces one write.</summary>
    public TimeSpan SaveDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    private void Schedule()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _debounce.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown raced the last edit; DisposeAsync already flushed.
        }
    }

    /// <summary>Writes the file immediately. Never throws — a preference is not worth an error dialog.</summary>
    public void SaveNow()
    {
        try
        {
            lock (_gate)
            {
                var folder = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Settings, Json));

                if (File.Exists(_path))
                {
                    File.Replace(temp, _path, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, _path);
                }
            }
        }
        catch
        {
            // A read-only or roaming-profile failure must never take the editor down.
        }
    }

    private static EditorSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(path), Json);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch
        {
            // A corrupt or unreadable file falls back to the defaults rather than blocking startup.
        }

        return new EditorSettings();
    }

    /// <summary>Flushes any pending change and stops the debounce timer.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounce.Dispose();
        SaveNow();
    }
}
