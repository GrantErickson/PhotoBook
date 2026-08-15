using System.Globalization;
using System.Text.Json;
using Microsoft.Graph.Models;
using PhotoBook.Core.Model;

namespace PhotoBook.Ingestion.OneDrive;

/// <summary>
/// Pulls OneDrive people tags for a drive item, so a named family member can become a top-priority
/// <see cref="FocusKind.Person"/> focus region (doc 05, ADR-0011).
/// <para>
/// <b>This is the spike seam.</b> Whether consumer OneDrive exposes people tags through Graph at all
/// is explicitly unverified (doc 05 "SPIKE: Graph people-tag availability"). The connector is written
/// against this interface precisely so the answer changes one adapter and not the pipeline: if tags
/// are unavailable the implementation returns nothing, local YuNet face detection carries the crop
/// logic, and only person <em>names</em> are missing.
/// </para>
/// </summary>
public interface IPeopleTagProvider
{
    /// <summary>Returns the people tags for an item, or an empty list when the source exposes none.</summary>
    /// <param name="item">The drive item being imported.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<PersonTag>> GetPersonTagsAsync(DriveItem item, CancellationToken ct = default);
}

/// <summary>The fallback: no people tags, ever. The documented floor of the spike, and a fully working app.</summary>
public sealed class NullPeopleTagProvider : IPeopleTagProvider
{
    /// <summary>The shared instance.</summary>
    public static NullPeopleTagProvider Instance { get; } = new();

    /// <inheritdoc/>
    public Task<IReadOnlyList<PersonTag>> GetPersonTagsAsync(DriveItem item, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PersonTag>>([]);
}

/// <summary>
/// Reads whatever people metadata Graph happens to attach to a <c>driveItem</c> — the optimistic half
/// of the spike. It inspects the item's untyped <see cref="DriveItem.AdditionalData"/> for the shapes
/// consumer OneDrive has been observed to use (a <c>tags</c> facet, a <c>people</c> collection, or
/// <c>listItem/fields</c> entries) and maps anything with a name onto a
/// <see cref="PersonTag"/>, including a normalized rectangle when one is present.
/// <para>
/// It never fails an import: unknown shapes yield an empty list, which is exactly the documented
/// fallback behavior.
/// </para>
/// </summary>
public sealed class GraphPeopleTagProvider : IPeopleTagProvider
{
    private static readonly string[] TagCollectionKeys = ["tags", "people", "peopleTags", "faces", "persons"];
    private static readonly string[] NameKeys = ["name", "displayName", "personName", "person", "title", "text"];
    private static readonly string[] RectKeys = ["rectangle", "rect", "boundingBox", "faceRectangle", "region"];

    /// <summary>The shared instance; extraction is stateless.</summary>
    public static GraphPeopleTagProvider Instance { get; } = new();

    /// <inheritdoc/>
    public Task<IReadOnlyList<PersonTag>> GetPersonTagsAsync(DriveItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();

        var tags = new List<PersonTag>();
        Collect(item.AdditionalData, tags);
        Collect(item.ListItem?.Fields?.AdditionalData, tags);

        return Task.FromResult<IReadOnlyList<PersonTag>>(tags);
    }

    private static void Collect(IDictionary<string, object>? data, List<PersonTag> tags)
    {
        if (data is null) return;

        foreach (var key in TagCollectionKeys)
        {
            if (!data.TryGetValue(key, out var value) || value is null) continue;
            if (value is not JsonElement element) continue;

            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var entry in element.EnumerateArray()) AddTag(entry, tags);
                    break;
                case JsonValueKind.Object:
                    AddTag(element, tags);
                    break;
                case JsonValueKind.String:
                    AddNamed(element.GetString(), null, tags);
                    break;
            }
        }
    }

    private static void AddTag(JsonElement entry, List<PersonTag> tags)
    {
        switch (entry.ValueKind)
        {
            case JsonValueKind.String:
                AddNamed(entry.GetString(), null, tags);
                return;
            case JsonValueKind.Object:
                break;
            default:
                return;
        }

        string? name = null;
        foreach (var key in NameKeys)
        {
            if (!entry.TryGetProperty(key, out var candidate)) continue;
            name = candidate.ValueKind switch
            {
                JsonValueKind.String => candidate.GetString(),
                JsonValueKind.Object => TryReadNestedName(candidate),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(name)) break;
        }

        Rect? rect = null;
        foreach (var key in RectKeys)
        {
            if (entry.TryGetProperty(key, out var candidate) && TryReadRect(candidate, out var parsed))
            {
                rect = parsed;
                break;
            }
        }

        AddNamed(name, rect, tags);
    }

    private static string? TryReadNestedName(JsonElement element)
    {
        foreach (var key in NameKeys)
        {
            if (element.TryGetProperty(key, out var candidate) && candidate.ValueKind == JsonValueKind.String)
                return candidate.GetString();
        }

        return null;
    }

    private static bool TryReadRect(JsonElement element, out Rect rect)
    {
        rect = default;
        if (element.ValueKind != JsonValueKind.Object) return false;

        if (!TryNumber(element, out var x, "x", "left") ||
            !TryNumber(element, out var y, "y", "top") ||
            !TryNumber(element, out var w, "w", "width") ||
            !TryNumber(element, out var h, "h", "height"))
        {
            return false;
        }

        var candidate = new Rect(x, y, w, h);
        // Person-tag rects are stored in normalized image coordinates (doc 05); anything else is
        // unusable without pixel dimensions, so it is dropped rather than guessed at.
        if (!candidate.IsWellFormed || !candidate.IsInsideUnitSquare) return false;

        rect = candidate;
        return true;
    }

    private static bool TryNumber(JsonElement element, out double value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var candidate)) continue;
            switch (candidate.ValueKind)
            {
                case JsonValueKind.Number when candidate.TryGetDouble(out value):
                    return true;
                case JsonValueKind.String when double.TryParse(candidate.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value):
                    return true;
            }
        }

        value = 0;
        return false;
    }

    private static void AddNamed(string? name, Rect? rect, List<PersonTag> tags)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmed = name.Trim();
        if (tags.Any(t => string.Equals(t.Name, trimmed, StringComparison.OrdinalIgnoreCase))) return;

        tags.Add(new PersonTag { Name = trimmed, Source = PersonTagSource.OneDrive, RegionRect = rect });
    }
}
