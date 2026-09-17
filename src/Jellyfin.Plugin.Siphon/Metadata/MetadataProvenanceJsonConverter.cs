using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Siphon.Identity;

namespace Jellyfin.Plugin.Siphon.Metadata;

/// <summary>Stores each observation once per title snapshot, not once per field. No history is appended.</summary>
public sealed class MetadataProvenanceJsonConverter : JsonConverter<Dictionary<string, MetadataFieldProvenance>>
{
    public override Dictionary<string, MetadataFieldProvenance> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid metadata provenance.");
        var result = new Dictionary<string, MetadataFieldProvenance>(StringComparer.Ordinal);
        if (!root.TryGetProperty("Sources", out var sources) || !root.TryGetProperty("Fields", out var fields))
        {
            if (!root.EnumerateObject().Any()) return result;
            throw new JsonException("Invalid metadata provenance schema.");
        }
        if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() > MetadataProvenance.Fields.Length * 4
            || fields.ValueKind != JsonValueKind.Object) throw new JsonException("Metadata provenance exceeds its bounds.");
        var observations = sources.Deserialize<MetadataObservation[]>(options) ?? [];
        if (observations.Any(source => source is null || source.Origin is not { Length: > 0 and <= 32 }
            || source.InstallationId is { Length: > 32 })) throw new JsonException("Invalid metadata observation.");
        root.TryGetProperty("Retained", out var retained);
        foreach (var field in fields.EnumerateObject())
        {
            if (!MetadataProvenance.Fields.Contains(field.Name, StringComparer.Ordinal)
                || field.Value.ValueKind != JsonValueKind.Array || field.Value.GetArrayLength() is < 1 or > 4
                || result.ContainsKey(field.Name)) throw new JsonException("Invalid metadata field provenance.");
            var evidence = new MetadataObservation[field.Value.GetArrayLength()];
            var offset = 0;
            foreach (var reference in field.Value.EnumerateArray())
            {
                if (!reference.TryGetInt32(out var index) || index < 0 || index >= observations.Length)
                    throw new JsonException("Invalid metadata source reference.");
                evidence[offset++] = observations[index];
            }
            var reason = retained.ValueKind == JsonValueKind.Object && retained.TryGetProperty(field.Name, out var reasonValue)
                && reasonValue.ValueKind == JsonValueKind.String ? reasonValue.GetString() : null;
            if (reason is { Length: > 64 }) throw new JsonException("Invalid metadata preservation reason.");
            result.Add(field.Name, new(evidence, reason));
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, MetadataFieldProvenance> value, JsonSerializerOptions options)
    {
        var fields = MetadataProvenance.Fields.Where(value.ContainsKey).ToArray();
        var sources = new List<MetadataObservation>();
        var indices = new Dictionary<MetadataObservation, int>();
        foreach (var field in fields)
            foreach (var source in value[field].Sources.Take(4))
                if (indices.TryAdd(source, sources.Count)) sources.Add(source);
        writer.WriteStartObject();
        writer.WritePropertyName("Sources");
        JsonSerializer.Serialize(writer, sources, options);
        writer.WriteStartObject("Fields");
        foreach (var field in fields)
        {
            if (value[field].Sources.Length == 0) continue;
            writer.WriteStartArray(field);
            foreach (var source in value[field].Sources.Take(4)) writer.WriteNumberValue(indices[source]);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        if (fields.Any(field => value[field].PreservationReason is not null))
        {
            writer.WriteStartObject("Retained");
            foreach (var field in fields)
                if (value[field].PreservationReason is { } reason) writer.WriteString(field, reason);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }
}
