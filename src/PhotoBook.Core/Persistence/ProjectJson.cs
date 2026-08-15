using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoBook.Core.Persistence;

/// <summary>
/// The one shared <see cref="JsonSerializerOptions"/> every project read and write goes through
/// (doc 04 §4): camelCase names, enums as camelCase strings, indented output for human-diffable
/// files, nulls omitted on write, and tolerance for hand-annotated files (comments and trailing
/// commas are accepted on read).
/// </summary>
public static class ProjectJson
{
    /// <summary>
    /// The canonical options instance. Shared and immutable — never mutate it; derive a copy with
    /// <c>new JsonSerializerOptions(ProjectJson.Options)</c> if a caller needs a variation.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Serializes a model to the project's canonical JSON text.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Deserializes project JSON text. Throws <see cref="JsonException"/> on malformed input and
    /// <see cref="ProjectFormatException"/> when the payload is a bare <c>null</c>.
    /// </summary>
    public static T Deserialize<T>(string json, string? fileName = null)
    {
        var value = JsonSerializer.Deserialize<T>(json, Options);
        if (value is null)
            throw new ProjectFormatException(fileName ?? typeof(T).Name, "The document is empty or null.");
        return value;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,                                   // human-diffable (doc 04 §1)
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,         // tolerate hand-annotation
            AllowTrailingCommas = true,                             // tolerate hand-editing
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            // Keep '#' colors, '×' in names and other non-ASCII readable rather than \u-escaped.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
