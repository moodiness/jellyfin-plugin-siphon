using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Identity;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Siphon.Metadata;

/// <summary>Bounded publication receipts distinguish native edits from later managed snapshots.</summary>
internal static class ItemMetadataOwnership
{
    internal sealed record Receipt(string Fingerprint, MetadataFieldProvenance Provenance);
    private const string Prefix = "SiphonField";
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 8 };

    internal static void Record(BaseItem target, string field, object? value, ManagedItem item, string managedField)
        => Record(target, field, value, item.MetadataProvenance.GetValueOrDefault(managedField)
            ?? new MetadataFieldProvenance([new("Unknown", null, null)]));

    internal static void Record(BaseItem target, string field, object? value, MetadataFieldProvenance provenance)
    {
        var receipt = new Receipt(Fingerprint(value), new(provenance.Sources.Take(4).ToArray(), provenance.PreservationReason));
        target.SetProviderId(Prefix + field, JsonSerializer.Serialize(receipt, Json));
    }

    internal static Receipt? Read(BaseItem target, string field)
    {
        var json = target.GetProviderId(Prefix + field);
        if (json is not { Length: > 0 and <= 4096 }) return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<Receipt>(json, Json);
            return receipt?.Fingerprint is { Length: 64 } && receipt.Provenance?.Sources is { Length: > 0 and <= 4 }
                ? receipt : null;
        }
        catch (JsonException) { return null; }
    }

    internal static string Fingerprint(object? value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    internal static object People(IEnumerable<PersonInfo> people)
        => people.Select(person => new { person.Name, Type = person.Type.ToString(), Role = person.Role ?? string.Empty })
            .OrderBy(person => person.Name, StringComparer.Ordinal).ThenBy(person => person.Type, StringComparer.Ordinal)
            .ThenBy(person => person.Role, StringComparer.Ordinal).ToArray();
}
