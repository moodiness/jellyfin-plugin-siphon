using System.Xml;
using System.Xml.Linq;
using Jellyfin.Plugin.Siphon.Identity;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Identity;

public sealed class NfoMetadataTests
{
    [Fact]
    public void MoviePreservesProviderNamespacesAndOwnershipWithoutRemoteImages()
    {
        var item = Item() with
        {
            ProviderIds = new Dictionary<string, string>
            {
                ["Tvdb"] = "123",
                ["Tmdb"] = "123",
                ["MyAnimeList"] = "456",
                ["Imdb"] = "tt1234567"
            },
            PosterUrl = "https://images.example/remote.jpg",
            Released = "2025-03-04T23:30:00-05:00",
            Year = 2025,
            Genres = ["Animation", "冒険"]
        };

        var xml = XElement.Parse(NfoMetadata.Movie(item));
        Assert.Equal("movie", xml.Name.LocalName);
        Assert.Equal("true", xml.Element("lockdata")?.Value);
        Assert.Equal("2025", xml.Element("year")?.Value);
        Assert.Equal("2025-03-04", xml.Element("premiered")?.Value);
        Assert.Equal(new[] { "Animation", "冒険" }, xml.Elements("genre").Select(element => element.Value));
        Assert.Equal("tt1234567", Id(xml, "Imdb"));
        Assert.Equal("123", Id(xml, "Tmdb"));
        Assert.Equal("123", Id(xml, "Tvdb"));
        Assert.Equal("456", Id(xml, "MyAnimeList"));
        Assert.Equal(item.ContentKey, Id(xml, "Siphon"));
        Assert.Null(xml.Element("season"));
        Assert.Null(xml.Element("episode"));
        Assert.DoesNotContain(item.PosterUrl, xml.ToString());
        Assert.Null(xml.Element("thumb"));
        Assert.Null(xml.Element("fanart"));

        var reordered = item with { ProviderIds = item.ProviderIds.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value) };
        Assert.Equal(NfoMetadata.Movie(item), NfoMetadata.Movie(reordered));
    }

    [Fact]
    public void EpisodeNeverInheritsSeriesIdsOrParentText()
    {
        var item = Item() with
        {
            Key = "series:tmdb:123:0:1",
            ContentKey = "series:tmdb:123",
            ContentId = "tmdb:tv:123",
            Type = "series",
            VideoId = "opaque:video-with-no-number",
            Name = "The special",
            Description = "Episode plot",
            SeriesName = "The series",
            SeriesDescription = "Series plot",
            ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt1234567", ["Tmdb"] = "123", ["MyAnimeList"] = "321" },
            EpisodeProviderIds = new Dictionary<string, string> { ["Tmdb"] = "987", ["Tvdb"] = "654" },
            Season = 0,
            Episode = 1,
            Released = "2024-02-29"
        };

        var episode = XElement.Parse(NfoMetadata.Episode(item));
        Assert.Equal("episodedetails", episode.Name.LocalName);
        Assert.Equal("The special", episode.Element("title")?.Value);
        Assert.Equal("Episode plot", episode.Element("plot")?.Value);
        Assert.Equal("true", episode.Element("lockdata")?.Value);
        Assert.Equal("0", episode.Element("season")?.Value);
        Assert.Equal("1", episode.Element("episode")?.Value);
        Assert.Equal("2024-02-29", episode.Element("aired")?.Value);
        Assert.Null(episode.Element("premiered"));
        Assert.Equal("987", Id(episode, "Tmdb"));
        Assert.Equal("654", Id(episode, "Tvdb"));
        Assert.Null(Id(episode, "Imdb"));
        Assert.Null(Id(episode, "MyAnimeList"));
        Assert.Equal(item.ContentKey, Id(episode, "Siphon"));

        var series = XElement.Parse(NfoMetadata.Series(item));
        Assert.Equal("tvshow", series.Name.LocalName);
        Assert.Equal("The series", series.Element("title")?.Value);
        Assert.Equal("Series plot", series.Element("plot")?.Value);
        Assert.Equal("true", series.Element("lockdata")?.Value);
        Assert.Equal("tt1234567", Id(series, "Imdb"));
        Assert.Equal("123", Id(series, "Tmdb"));
        Assert.Equal("321", Id(series, "MyAnimeList"));
        Assert.Null(Id(series, "Tvdb"));
        Assert.Equal(item.ContentKey, Id(series, "Siphon"));
        Assert.Null(series.Element("season"));
        Assert.Null(series.Element("episode"));
        Assert.Null(series.Element("aired"));
        Assert.Null(series.Element("premiered"));

        var unidentifiedEpisode = XElement.Parse(NfoMetadata.Episode(item with { EpisodeProviderIds = [] }));
        Assert.Equal("Siphon", Assert.Single(unidentifiedEpisode.Elements("uniqueid")).Attribute("type")?.Value);
    }

    [Fact]
    public void XmlEscapingPreservesTextWithoutCreatingMetadataElements()
    {
        const string title = "夏 & <title>\"Quoted\" '名前'";
        const string plot = "</plot><uniqueid type=\"Imdb\">tt9999999</uniqueid>\r\n\tRésumé & 続き";
        var item = Item() with
        {
            Name = title,
            Description = plot,
            Genres = ["<genre>&文字</genre>"],
            ContentKey = "movie:opaque<&\"owned\">"
        };

        var xml = XElement.Parse(NfoMetadata.Movie(item), LoadOptions.PreserveWhitespace);
        Assert.Equal(title, xml.Element("title")?.Value);
        Assert.Equal(plot, xml.Element("plot")?.Value);
        Assert.Equal("<genre>&文字</genre>", Assert.Single(xml.Elements("genre")).Value);
        Assert.Null(Id(xml, "Imdb"));
        Assert.Equal(item.ContentKey, Id(xml, "Siphon"));
        Assert.Empty(xml.Element("plot")!.Elements());
        Assert.Equal("Siphon", Assert.Single(xml.Elements("uniqueid")).Attribute("type")?.Value);
    }

    [Fact]
    public void InvalidXmlCharactersAreRemovedWithoutLosingSupplementaryUnicode()
    {
        const string clean = "日本語\U00020000\t\n\rالعربية";
        var item = Item() with
        {
            Name = clean + "\0\u0001\uD800x\uDC00\uFFFE\uFFFF",
            Description = "\u0002" + clean + "\uD800",
            Genres = ["\u0001冒険\U00020000\uDC00"]
        };

        var xml = XElement.Parse(NfoMetadata.Movie(item), LoadOptions.PreserveWhitespace);
        Assert.Equal(clean + "x", xml.Element("title")?.Value);
        Assert.Equal(clean, xml.Element("plot")?.Value);
        Assert.Equal("冒険\U00020000", Assert.Single(xml.Elements("genre")).Value);
    }

    [Fact]
    public void InvalidIdentityCannotBeSilentlySanitizedOrOverrideOwnership()
    {
        var item = Item();
        Assert.Throws<XmlException>(() => NfoMetadata.Movie(item with { ContentKey = "movie:bad\0key" }));
        Assert.Throws<XmlException>(() => NfoMetadata.Movie(item with { ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "12\0" } }));
        Assert.Throws<ArgumentException>(() => NfoMetadata.Movie(item with { ProviderIds = new Dictionary<string, string> { ["Siphon"] = "forged-owner" } }));
        Assert.Throws<ArgumentException>(() => NfoMetadata.Movie(item with { ProviderIds = new Dictionary<string, string> { ["Tmdb"] = " " } }));
    }

    [Fact]
    public void InvalidDatesAndNumberingAreRejectedInsteadOfInvented()
    {
        var item = Item();
        Assert.Throws<FormatException>(() => NfoMetadata.Movie(item with { Released = "2025-02-29" }));
        Assert.Throws<FormatException>(() => NfoMetadata.Movie(item with { Released = "03/04/2025" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => NfoMetadata.Movie(item with { Year = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => NfoMetadata.Episode(item with { Season = -1, Episode = 1 }));

        var xml = XElement.Parse(NfoMetadata.Episode(item with { VideoId = "tt1234567:2:9" }));
        Assert.Null(xml.Element("season"));
        Assert.Null(xml.Element("episode"));
        Assert.Null(xml.Element("aired"));
        Assert.Null(xml.Element("year"));
    }

    private static string? Id(XElement xml, string provider) => xml.Elements("uniqueid")
        .SingleOrDefault(element => element.Attribute("type")?.Value == provider)?.Value;

    private static ManagedItem Item() => new()
    {
        Key = "movie:opaque:sample",
        Type = "movie",
        ContentKey = "movie:opaque:sample",
        ContentId = "addon:sample",
        VideoId = "addon:sample",
        Name = "Film",
        Path = "/library/Film.strm"
    };
}
