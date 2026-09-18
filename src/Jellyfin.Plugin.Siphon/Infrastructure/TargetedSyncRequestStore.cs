using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

public sealed record SyncTarget(string? CatalogKey = null, string? ContentKey = null)
{
    public static string CatalogIdentity(string addonId, string subscriptionKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(addonId.Length + ":" + addonId + subscriptionKey)));

    public void Validate(PluginConfiguration config, IReadOnlyCollection<ManagedItem> items)
        => Validate(config, ContentKey is not null && items.Any(item => item.Type == "series" && !item.IsSearchPreview && item.ContentKey == ContentKey));

    public void Validate(PluginConfiguration config, SiphonReadSnapshot snapshot)
        => Validate(config, ContentKey is not null && snapshot.Items.Any(item => item.Type == "series" && !item.IsSearchPreview && item.ContentKey == ContentKey));

    private void Validate(PluginConfiguration config, bool containsSeries)
    {
        if ((CatalogKey is null) == (ContentKey is null)
            || CatalogKey is not null && string.IsNullOrWhiteSpace(CatalogKey)
            || ContentKey is not null && string.IsNullOrWhiteSpace(ContentKey)
            || CatalogKey is { Length: > 256 } || ContentKey is { Length: > 256 })
            throw new ArgumentException("Select exactly one enabled catalog or existing series.");
        if (CatalogKey is not null && config.Addons.Where(addon => addon.Enabled)
            .SelectMany(addon => addon.Catalogs.Where(catalog => catalog.Enabled)
                .Select(catalog => CatalogIdentity(addon.Id, catalog.Key))).Count(key => key == CatalogKey) != 1)
            throw new KeyNotFoundException("The catalog is missing, disabled, or ambiguous. Reload the target list and select an enabled catalog.");
        if (ContentKey is not null && !containsSeries)
            throw new KeyNotFoundException("The series is no longer in Siphon. Reload the target list and select an existing series.");
    }
}

/// <summary>One native scheduled task owns the accepted request until its execution finishes.</summary>
public sealed class TargetedSyncRequestStore
{
    private readonly object _gate = new();
    private SyncTarget? _target;
    private bool _executing;

    public bool TryQueue(SyncTarget target)
    {
        lock (_gate)
        {
            if (_target is not null) return false;
            _target = target;
            return true;
        }
    }

    public SyncTarget? BeginExecution()
    {
        lock (_gate)
        {
            if (_executing || _target is null) return null;
            _executing = true;
            return _target;
        }
    }

    public void Complete()
    {
        lock (_gate) { _target = null; _executing = false; }
    }
}
