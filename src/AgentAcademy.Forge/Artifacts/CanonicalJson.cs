using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentAcademy.Forge.Artifacts;

/// <summary>
/// Deterministic JSON serialization and SHA-256 hashing.
/// Canonical form: sorted keys, no whitespace, UTF-8.
/// </summary>
public static class CanonicalJson
{
    /// <summary>
    /// Serialize a JsonElement to canonical JSON (sorted keys, no whitespace).
    /// </summary>
    public static string Serialize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        WriteCanonical(writer, element);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Serialize a typed object to canonical JSON via intermediate JsonElement.
    /// </summary>
    public static string Serialize<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, SerializerOptions);
        using var doc = JsonDocument.Parse(json);
        return Serialize(doc.RootElement);
    }

    /// <summary>
    /// Compute SHA-256 hash of canonical JSON for a JsonElement.
    /// Returns lowercase hex string (no prefix).
    /// </summary>
    public static string Hash(JsonElement element)
    {
        var canonical = Serialize(element);
        return HashString(canonical);
    }

    /// <summary>
    /// Compute SHA-256 hash of canonical JSON for a typed object.
    /// Returns lowercase hex string (no prefix).
    /// </summary>
    public static string Hash<T>(T value)
    {
        var canonical = Serialize(value);
        return HashString(canonical);
    }

    /// <summary>
    /// Returns hash with "sha256:" prefix for use in trace contracts.
    /// </summary>
    public static string PrefixedHash<T>(T value) => $"sha256:{Hash(value)}";

    /// <summary>
    /// Returns hash with "sha256:" prefix for use in trace contracts.
    /// </summary>
    public static string PrefixedHash(JsonElement element) => $"sha256:{Hash(element)}";

    /// <summary>
    /// Strip "sha256:" prefix from a prefixed hash, returning raw hex.
    /// </summary>
    public static string StripPrefix(string prefixedHash)
    {
        const string prefix = "sha256:";
        return prefixedHash.StartsWith(prefix, StringComparison.Ordinal)
            ? prefixedHash[prefix.Length..]
            : prefixedHash;
    }

    private static string HashString(string canonical)
    {
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                // Sort properties by key for deterministic output
                var properties = element.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToList();
                foreach (var prop in properties)
                {
                    writer.WritePropertyName(prop.Name);
                    WriteCanonical(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new ArgumentException($"Unsupported JsonValueKind: {element.ValueKind}");
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
