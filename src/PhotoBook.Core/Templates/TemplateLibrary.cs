using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;

namespace PhotoBook.Core.Templates;

/// <summary>
/// The shipped layout library: every <see cref="Template"/> embedded in this assembly as
/// <c>Templates/{id}.json</c>, parsed once, ordered by <see cref="Template.Id"/> and served
/// in-memory (doc 07 "Templates are data, not code").
/// <para>
/// Ordering is ordinal-by-id and therefore independent of file-system and manifest enumeration
/// order — that determinism is what lets the engine promise same-inputs-same-book (kernel §7).
/// </para>
/// <para>
/// The library is <b>immutable by contract</b>: nothing here ever hands out a template that a
/// caller is expected to mutate. <see cref="Mirror"/> and <see cref="ForPage"/> return deep copies,
/// so a page that mirrors or detaches a layout can never write back into the shipped one (doc 07
/// "The library is immutable").
/// </para>
/// </summary>
public sealed class TemplateLibrary
{
    /// <summary>Manifest-resource prefix every embedded template file shares.</summary>
    public const string ResourcePrefix = "PhotoBook.Core.Templates.";

    /// <summary>The one schema version this loader understands (doc 07 "Template JSON schema").</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>
    /// The shared project options, tightened for library templates only: an unknown field here means
    /// the binary and the shipped JSON have drifted apart, which is a load error rather than
    /// something to round-trip (doc 07 "Template JSON schema"). Detached snapshots stay on the
    /// permissive <see cref="ProjectJson.Options"/>, which preserves unknown fields.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions =
        new(ProjectJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private static readonly Lazy<TemplateLibrary> Lazy =
        new(() => Load(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Dictionary<string, Template> _byId;

    private TemplateLibrary(IReadOnlyList<Template> templates)
    {
        Templates = templates;
        _byId = templates.ToDictionary(t => t.Id!, StringComparer.Ordinal);
        Ids = new ReadOnlyCollection<string>(templates.Select(t => t.Id!).ToList());
    }

    /// <summary>The process-wide library, parsed on first use.</summary>
    public static TemplateLibrary Default => Lazy.Value;

    /// <summary>Every template, ordered by ordinal <see cref="Template.Id"/>.</summary>
    public IReadOnlyList<Template> Templates { get; }

    /// <summary>Every template id, in the same order as <see cref="Templates"/>.</summary>
    public IReadOnlyList<string> Ids { get; }

    /// <summary>How many templates the library holds.</summary>
    public int Count => Templates.Count;

    /// <summary>
    /// Reads every embedded template resource of <paramref name="assembly"/> (this assembly by
    /// default) and returns them ordered by id.
    /// </summary>
    /// <exception cref="TemplateLoadException">
    /// A resource is not valid template JSON, declares an unsupported <c>schemaVersion</c>, carries
    /// an unknown field, has no id, has an id that disagrees with its file name, or duplicates
    /// another template's id.
    /// </exception>
    public static TemplateLibrary Load(Assembly? assembly = null)
    {
        assembly ??= typeof(TemplateLibrary).Assembly;

        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                        n.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var templates = new List<Template>(names.Count);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new TemplateLoadException(name, "the manifest resource stream is missing.");
            using var reader = new StreamReader(stream);
            var template = Parse(reader.ReadToEnd(), name);

            var expectedId = name[ResourcePrefix.Length..^".json".Length];
            if (!string.Equals(template.Id, expectedId, StringComparison.Ordinal))
            {
                throw new TemplateLoadException(
                    name, $"declares id '{template.Id}' but is stored as '{expectedId}.json'; " +
                          "file name and id must agree so the load order is reproducible.");
            }

            if (seen.TryGetValue(expectedId, out var other))
            {
                throw new TemplateLoadException(name, $"duplicates the id already loaded from '{other}'.");
            }

            seen.Add(expectedId, name);
            templates.Add(template);
        }

        templates.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return new TemplateLibrary(new ReadOnlyCollection<Template>(templates));
    }

    /// <summary>
    /// Parses one template document. Applies the doc 07 load-time gates: schema version, required
    /// id, and rejection of unknown fields.
    /// </summary>
    /// <param name="json">The template document.</param>
    /// <param name="sourceName">A name used in error messages — a resource or file name.</param>
    /// <exception cref="TemplateLoadException">The document is unreadable or fails a load-time gate.</exception>
    public static Template Parse(string json, string? sourceName = null)
    {
        var source = sourceName ?? "<template json>";

        Template? template;
        try
        {
            template = JsonSerializer.Deserialize<Template>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new TemplateLoadException(source, $"is not valid template JSON: {ex.Message}", ex);
        }

        if (template is null)
        {
            throw new TemplateLoadException(source, "deserialized to null.");
        }

        if (template.SchemaVersion != SupportedSchemaVersion)
        {
            throw new TemplateLoadException(
                source, $"declares schemaVersion {template.SchemaVersion}; " +
                        $"this build understands {SupportedSchemaVersion} only.");
        }

        if (string.IsNullOrWhiteSpace(template.Id))
        {
            throw new TemplateLoadException(source, "has no id; library templates must be identifiable.");
        }

        return template;
    }

    /// <summary>True when the library holds a template with this id.</summary>
    public bool Contains(string id) => _byId.ContainsKey(id);

    /// <summary>The template with this id.</summary>
    /// <exception cref="KeyNotFoundException">No template has that id.</exception>
    public Template this[string id] =>
        _byId.TryGetValue(id, out var t)
            ? t
            : throw new KeyNotFoundException($"No template with id '{id}' in the library.");

    /// <summary>The template with this id, or null.</summary>
    public Template? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Looks a template up by id.</summary>
    public bool TryGet(string id, [NotNullWhen(true)] out Template? template) =>
        _byId.TryGetValue(id, out template);

    /// <summary>Every template of one kind, in library order.</summary>
    public IReadOnlyList<Template> ByKind(TemplateKind kind) =>
        Templates.Where(t => t.Kind == kind).ToList();

    /// <summary>Every template holding exactly <paramref name="photoCount"/> photos, in library order.</summary>
    public IReadOnlyList<Template> ByPhotoCount(int photoCount) =>
        Templates.Where(t => t.PhotoCount == photoCount).ToList();

    /// <summary>Both sides of a spread pair, in library order (left before right by id).</summary>
    public IReadOnlyList<Template> ByPairId(string pairId) =>
        Templates.Where(t => string.Equals(t.Pair?.PairId, pairId, StringComparison.Ordinal)).ToList();

    /// <summary>
    /// The doc 07 mirror rule — <c>x' = 1 − x − w</c> applied to every image slot and text slot rect
    /// — as a pure geometric transform on a deep copy. Ids, ordering, aspects, tier affinities,
    /// caption policies, sections and text alignment are untouched: a placement survives its page
    /// changing sides, and journal text stays left-aligned because reading direction beats symmetry.
    /// <para>
    /// This applies the flip unconditionally; it does not consult <see cref="Template.Mirrorable"/>.
    /// Use <see cref="ForPage"/> for the page-side decision the engine actually makes.
    /// </para>
    /// </summary>
    public static Template Mirror(Template template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var copy = DeepCopy(template);
        foreach (var slot in copy.Slots) slot.Rect = slot.Rect.Mirrored();
        foreach (var text in copy.TextSlots) text.Rect = text.Rect.Mirrored();
        return copy;
    }

    /// <summary>
    /// The template as it should render on one side of a spread: mirrored when the page is a left
    /// page and the template is <see cref="Template.Mirrorable"/>, an unmodified deep copy
    /// otherwise. Templates are authored as right pages with the gutter at <c>x = 0</c> (doc 07).
    /// </summary>
    public static Template ForPage(Template template, bool leftPage)
    {
        ArgumentNullException.ThrowIfNull(template);
        return leftPage && template.Mirrorable ? Mirror(template) : DeepCopy(template);
    }

    /// <summary>
    /// The library template with this id, oriented for the given page side — see
    /// <see cref="ForPage(Template, bool)"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No template has that id.</exception>
    public Template ForPage(string id, bool leftPage) => ForPage(this[id], leftPage);

    /// <summary>
    /// Lints every template plus the library-wide rules (unique ids, spread-pair integrity), most
    /// severe first. An empty result means the library is printable (doc 07 "Template linter").
    /// </summary>
    public IReadOnlyList<TemplateDiagnostic> Lint() => TemplateLinter.LintLibrary(Templates);

    private static Template DeepCopy(Template template) => template with
    {
        Slots = template.Slots.Select(s => s with { }).ToList(),
        TextSlots = template.TextSlots.Select(t => t with { }).ToList(),
        Sections = template.Sections?.Select(s => s with
        {
            SlotIds = new List<string>(s.SlotIds),
            TextSlotIds = new List<string>(s.TextSlotIds),
        }).ToList(),
        Pair = template.Pair is null ? null : template.Pair with { },
    };
}

/// <summary>Thrown when an embedded template resource cannot be turned into a <see cref="Template"/>.</summary>
public sealed class TemplateLoadException : Exception
{
    /// <summary>Creates the exception for a named source.</summary>
    public TemplateLoadException(string source, string problem, Exception? inner = null)
        : base($"Template '{source}' {problem}", inner)
    {
        ResourceName = source;
    }

    /// <summary>The resource or file name that failed to load.</summary>
    public string ResourceName { get; }
}
