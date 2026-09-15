using System.Text;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Identity;

/// <summary>Writes only owned shortcuts and asks Jellyfin's scanner to ingest them.</summary>
public sealed class LibraryWriter(
    ConfigurationAccessor configuration,
    CapabilityTokenService tokens,
    ISiphonStateStore state,
    ILibraryManager library,
    ILibraryMonitor monitor,
    IFileSystem fileSystem,
    ISafeHttpClient http,
    ILogger<LibraryWriter> logger)
{
    private const string Marker = ".siphon-owned";

    public void ValidateTargets()
    {
        var config = configuration.Current;
        ConfigurationValidator.Validate(config);
        if (string.IsNullOrWhiteSpace(config.PublicBaseUrl))
        {
            throw new InvalidOperationException("Set PublicBaseUrl before synchronizing Siphon.");
        }

        var folders = library.GetVirtualFolders();
        foreach (var (root, type) in new[] { (config.MoviesRootPath, CollectionTypeOptions.movies), (config.TvShowsRootPath, CollectionTypeOptions.tvshows) })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            ManagedPath.EnsureNoLinks(root);
            // A physical library location avoids an extra folder being mistaken for a TV series.
            var target = folders.FirstOrDefault(f => f.CollectionType == type
                && f.Locations.Any(location => Path.GetFullPath(location).Equals(Path.GetFullPath(root), ManagedPath.Comparison)));
            if (target is null)
            {
                throw new InvalidOperationException($"Register the Siphon {type} root as a folder location of an existing {type} library first.");
            }

            Directory.CreateDirectory(root);
            var markerPath = Path.Combine(root, Marker);
            if (!File.Exists(markerPath))
            {
                if (Directory.EnumerateFileSystemEntries(root).Any())
                {
                    throw new InvalidOperationException("A new Siphon root must be empty. Existing local media must use a separate library folder location.");
                }

                File.WriteAllText(markerPath, Plugin.PluginId.ToString("N"), Encoding.UTF8);
            }
            else if (File.ReadAllText(markerPath).Trim() != Plugin.PluginId.ToString("N"))
            {
                throw new InvalidOperationException("The managed root ownership marker is invalid.");
            }
        }

        foreach (var item in state.GetItems())
        {
            var root = item.Type == "movie" ? config.MoviesRootPath : config.TvShowsRootPath;
            if (string.IsNullOrWhiteSpace(root) || !ManagedPath.IsWithin(item.Path, root))
            {
                throw new InvalidOperationException("Managed roots cannot be moved while Siphon items exist. Keep the original roots to preserve media identity.");
            }
        }
    }

    public string GetPath(string type, string contentKey, IReadOnlyDictionary<string, string> ids, string title, int? year, int? season, int? episode)
    {
        var root = type == "movie" ? configuration.Current.MoviesRootPath : configuration.Current.TvShowsRootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException($"Configure a managed root for {type} items before subscribing to this catalog.");
        }

        var suffix = ids.TryGetValue("Imdb", out var imdb) ? $"[imdbid-{imdb}]"
            : ids.TryGetValue("Tmdb", out var tmdb) ? $"[tmdbid-{tmdb}]"
            : ids.TryGetValue("Tvdb", out var tvdb) ? $"[tvdbid-{tvdb}]"
            : "[siphon-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(contentKey)))[..16] + "]";
        var name = ManagedPath.SafeName(title) + (year is null ? string.Empty : $" ({year})") + " " + suffix;
        if (type == "movie")
        {
            return Path.Combine(Path.GetFullPath(root), name, name + ".strm");
        }

        var priorEpisode = state.FindByContentKey(contentKey);
        var seriesDirectory = priorEpisode is null ? Path.Combine(Path.GetFullPath(root), name)
            : Path.GetDirectoryName(Path.GetDirectoryName(priorEpisode.Path))!;
        return Path.Combine(seriesDirectory, $"Season {season:00}", $"Episode S{season:00}E{episode:00}.strm");
    }

    public async Task<int> WriteAsync(IReadOnlyList<ManagedItem> items, CancellationToken cancellationToken)
    {
        var writes = 0;
        var roots = items.Select(GetOwnedRoot).Distinct().ToArray();
        var writtenSeries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots) monitor.ReportFileSystemChangeBeginning(root);
        try
        {
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.GetDirectoryName(item.Path)!;
                ManagedPath.EnsureNoLinks(directory);
                if (new FileInfo(item.Path).LinkTarget is not null)
                {
                    throw new IOException("Managed shortcuts cannot be symbolic links.");
                }

                var url = configuration.Current.PublicBaseUrl.TrimEnd('/') + "/Siphon/s/" + tokens.SignItem(item.Key) + "\n";
                if (File.Exists(item.Path) && (state.FindByPath(item.Path)?.Key != item.Key || !IsOwnedShortcut(item.Path, item.Key)))
                {
                    throw new IOException("Refusing to overwrite a shortcut not owned by Siphon.");
                }

                Directory.CreateDirectory(directory);
                if (item.Type == "series")
                {
                    var seriesDirectory = Path.GetDirectoryName(directory)!;
                    if (writtenSeries.Add(item.ContentKey))
                    {
                        await WriteNfoAsync(Path.Combine(seriesDirectory, "tvshow.nfo"), NfoMetadata.Series(item), item.ContentKey, cancellationToken).ConfigureAwait(false);
                        await WritePosterAsync(seriesDirectory, item, cancellationToken).ConfigureAwait(false);
                    }

                    await WriteNfoAsync(Path.ChangeExtension(item.Path, ".nfo"), NfoMetadata.Episode(item), item.ContentKey, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await WriteNfoAsync(Path.ChangeExtension(item.Path, ".nfo"), NfoMetadata.Movie(item), item.ContentKey, cancellationToken).ConfigureAwait(false);
                    await WritePosterAsync(directory, item, cancellationToken).ConfigureAwait(false);
                }

                if (await WriteTextIfChangedAsync(item.Path, url, cancellationToken).ConfigureAwait(false)) writes++;
            }
        }
        finally
        {
            foreach (var root in roots) monitor.ReportFileSystemChangeComplete(root, false);
        }

        return writes;
    }

    public void Delete(ManagedItem item)
    {
        var root = GetOwnedRoot(item);
        ManagedPath.EnsureNoLinks(Path.GetDirectoryName(item.Path)!);
        if (state.FindByPath(item.Path)?.Key != item.Key || (File.Exists(item.Path) && !IsOwnedShortcut(item.Path, item.Key)))
        {
            throw new IOException("Refusing to remove an item not owned by Siphon.");
        }

        monitor.ReportFileSystemChangeBeginning(root);
        try
        {
            if (new FileInfo(item.Path).LinkTarget is not null)
            {
                throw new IOException("Managed shortcuts cannot be symbolic links.");
            }

            File.Delete(item.Path);
            var nfo = Path.ChangeExtension(item.Path, ".nfo");
            if (File.Exists(nfo) && new FileInfo(nfo).LinkTarget is null && IsOwnedNfo(nfo, item.ContentKey)) File.Delete(nfo);
            var existing = library.FindByPath(item.Path, false);
            if (existing is not null)
            {
                library.DeleteItem(existing, new DeleteOptions { DeleteFileLocation = false });
            }
        }
        finally
        {
            monitor.ReportFileSystemChangeComplete(root, false);
        }
    }

    public async Task IngestAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var roots = new[] { configuration.Current.MoviesRootPath, configuration.Current.TvShowsRootPath }
            .Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        for (var index = 0; index < roots.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = roots[index];
            var folder = library.FindByPath(root, true) as Folder;
            if (folder is null)
            {
                await library.ValidateTopLibraryFolders(cancellationToken).ConfigureAwait(false);
                folder = library.FindByPath(root, true) as Folder;
            }

            if (folder is null)
            {
                throw new InvalidOperationException("Jellyfin has not indexed the managed library root. Save the library folder settings first.");
            }

            var rootIndex = index;
            var childProgress = new Progress<double>(value => progress.Report((rootIndex * 100d + value) / roots.Length));
            var options = new MetadataRefreshOptions(new DirectoryService(fileSystem))
            {
                EnableRemoteContentProbe = false
            };
            // Recursion is limited to this dedicated Siphon location, not the entire Jellyfin library.
            await folder.ValidateChildren(childProgress, options, recursive: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Siphon ingested managed library location {LocationIndex}", index + 1);
        }
    }

    private static async Task<bool> WriteTextIfChangedAsync(string path, string content, CancellationToken ct)
    {
        if (File.Exists(path) && await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) == content) return false;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }

        return true;
    }

    private static async Task WriteNfoAsync(string path, string content, string contentKey, CancellationToken ct)
    {
        if (new FileInfo(path).LinkTarget is not null || (File.Exists(path) && !IsOwnedNfo(path, contentKey)))
        {
            throw new IOException("Refusing to replace an NFO not owned by Siphon.");
        }

        await WriteTextIfChangedAsync(path, content, ct).ConfigureAwait(false);
    }

    private static bool IsOwnedNfo(string path, string contentKey)
    {
        if (new FileInfo(path).Length > 1024 * 1024) return false;
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        return document.Root?.Elements("uniqueid").Any(e => (string?)e.Attribute("type") == "Siphon" && e.Value == contentKey) == true;
    }

    private async Task WritePosterAsync(string directory, ManagedItem item, CancellationToken ct)
    {
        if (!Uri.TryCreate(item.PosterUrl, UriKind.Absolute, out var uri)) return;
        if (new[] { ".jpg", ".png", ".webp" }.Any(extension => File.Exists(Path.Combine(directory, "poster" + extension)))) return;
        string? temporary = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(configuration.Current.AddonTimeoutSeconds));
            using var response = await http.SendAsync(uri, HttpMethod.Get, null, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var image = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (image.Length + count > 5 * 1024 * 1024) throw new InvalidDataException("Poster exceeds size limit.");
                image.Write(buffer, 0, count);
            }

            var bytes = image.GetBuffer();
            var extension = image.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff ? ".jpg"
                : image.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? ".png"
                : image.Length >= 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP" ? ".webp" : null;
            if (extension is null) throw new InvalidDataException("Unsupported poster format.");
            temporary = Path.Combine(directory, "siphon-poster-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await output.WriteAsync(bytes.AsMemory(0, (int)image.Length), deadline.Token).ConfigureAwait(false);
            }

            File.Move(temporary, Path.Combine(directory, "poster" + extension), false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            logger.LogWarning("Siphon poster unavailable for item {ItemKey}: {ErrorType}", item.Key, exception.GetType().Name);
        }
        finally
        {
            if (temporary is not null) File.Delete(temporary);
        }
    }

    private bool IsOwnedShortcut(string path, string key)
    {
        var file = new FileInfo(path);
        if (file.Length > 8192)
        {
            return false;
        }

        var text = File.ReadAllText(path).Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var segment = uri.AbsolutePath.Split('/').LastOrDefault();
        return segment is not null && tokens.TryReadItem(segment, out var itemKey) && itemKey == key;
    }

    private string GetOwnedRoot(ManagedItem item)
    {
        var root = item.Type == "movie" ? configuration.Current.MoviesRootPath : configuration.Current.TvShowsRootPath;
        if (string.IsNullOrWhiteSpace(root) || !ManagedPath.IsWithin(item.Path, root)
            || !File.Exists(Path.Combine(root, Marker)))
        {
            throw new IOException("Item path is outside a Siphon-owned library root.");
        }

        return root;
    }
}
