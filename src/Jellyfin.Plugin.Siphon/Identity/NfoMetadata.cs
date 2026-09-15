using System.Globalization;
using System.Text;
using System.Xml;

namespace Jellyfin.Plugin.Siphon.Identity;

/// <summary>Serializes addon metadata for Jellyfin's native NFO scanner.</summary>
public static class NfoMetadata
{
    private static readonly string[] ReleasedFormats = ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];

    public static string Movie(ManagedItem item) => Serialize(item, "movie", item.Name, item.Description, item.ProviderIds);

    public static string Series(ManagedItem item) => Serialize(item, "tvshow", item.SeriesName, item.SeriesDescription, item.ProviderIds);

    public static string Episode(ManagedItem item) => Serialize(item, "episodedetails", item.Name, item.Description, item.EpisodeProviderIds);

    private static string Serialize(ManagedItem item, string root, string? title, string? plot, IReadOnlyDictionary<string, string> providerIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.ContentKey);
        XmlConvert.VerifyXmlChars(item.ContentKey);
        if (item.Year is < 1 or > 9999)
        {
            throw new ArgumentOutOfRangeException(nameof(item), "The metadata year must be between 1 and 9999.");
        }

        if (root == "episodedetails" && (item.Season is < 0 || item.Episode is < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(item), "Episode and season numbers cannot be negative.");
        }

        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true,
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Entitize
        }))
        {
            writer.WriteStartElement(root);
            writer.WriteElementString("lockdata", "true");
            WriteText(writer, "title", title);
            WriteText(writer, "plot", plot);
            if (item.Year is { } year)
            {
                writer.WriteElementString("year", year.ToString(CultureInfo.InvariantCulture));
            }

            // Released belongs to the playable item, not necessarily its parent series.
            if (root != "tvshow" && !string.IsNullOrWhiteSpace(item.Released))
            {
                var released = DateTimeOffset.ParseExact(item.Released, ReleasedFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                writer.WriteElementString(root == "episodedetails" ? "aired" : "premiered", released.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            if (root == "episodedetails")
            {
                if (item.Season is { } season)
                {
                    writer.WriteElementString("season", season.ToString(CultureInfo.InvariantCulture));
                }

                if (item.Episode is { } episode)
                {
                    writer.WriteElementString("episode", episode.ToString(CultureInfo.InvariantCulture));
                }
            }

            foreach (var genre in item.Genres)
            {
                WriteText(writer, "genre", genre);
            }

            foreach (var (provider, id) in providerIds.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (provider is not ("Imdb" or "Tmdb" or "Tvdb" or "MyAnimeList"))
                {
                    throw new ArgumentException("Unsupported NFO provider namespace: " + provider, nameof(item));
                }

                ArgumentException.ThrowIfNullOrWhiteSpace(id);
                XmlConvert.VerifyXmlChars(id);
                WriteId(writer, provider, id);
            }

            // The ownership marker always identifies the movie or series, never an episode ID.
            WriteId(writer, "Siphon", item.ContentKey);
            writer.WriteEndElement();
        }

        return text.ToString();
    }

    private static void WriteId(XmlWriter writer, string provider, string id)
    {
        writer.WriteStartElement("uniqueid");
        writer.WriteAttributeString("type", provider);
        writer.WriteString(id);
        writer.WriteEndElement();
    }

    private static void WriteText(XmlWriter writer, string element, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteElementString(element, CleanText(value));
        }
    }

    private static string CleanText(string value)
    {
        StringBuilder? clean = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                clean?.Append(character).Append(value[index + 1]);
                index++;
            }
            else if (XmlConvert.IsXmlChar(character))
            {
                clean?.Append(character);
            }
            else
            {
                clean ??= new StringBuilder(value.Length).Append(value, 0, index);
            }
        }

        return clean?.ToString() ?? value;
    }
}
