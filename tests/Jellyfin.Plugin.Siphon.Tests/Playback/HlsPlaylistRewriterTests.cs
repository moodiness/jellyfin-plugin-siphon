using Jellyfin.Plugin.Siphon.Playback;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Playback;

public sealed class HlsPlaylistRewriterTests
{
    [Fact]
    public void RewritesSegmentsAndEveryResourceAttributeAgainstFinalRedirect()
    {
        const string playlist = "#EXTM3U\n#secret https://private.example/credential\n#EXT-X-KEY:METHOD=AES-128,URI=\"../key?secret=abc\"\n#EXT-X-MAP:URI=\"init.mp4\",BYTERANGE=\"100@0\"\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",URI=\"//audio.example/live.m3u8\"\n#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=100,URI=\"iframe.m3u8\"\n#EXT-X-SESSION-KEY:METHOD=AES-128,URI=\"https://key.example/key\"\n#EXTINF:5,\nsegment.ts?token=secret\n";
        var resources = new List<Uri>();
        var rewritten = HlsPlaylistRewriter.Rewrite(playlist, new Uri("https://origin.example/redirected/master.m3u8"), uri =>
        {
            resources.Add(uri);
            return "/Siphon/media/opaque" + resources.Count;
        });

        Assert.Equal(new[]
        {
            "https://origin.example/key?secret=abc",
            "https://origin.example/redirected/init.mp4",
            "https://audio.example/live.m3u8",
            "https://origin.example/redirected/iframe.m3u8",
            "https://key.example/key",
            "https://origin.example/redirected/segment.ts?token=secret"
        }, resources.Select(u => u.AbsoluteUri));
        Assert.DoesNotContain("https:", rewritten);
        Assert.DoesNotContain("secret", rewritten);
        Assert.Contains("BYTERANGE=\"100@0\"", rewritten);
        Assert.Contains("URI=\"/Siphon/media/opaque5\"", rewritten);
        Assert.EndsWith("/Siphon/media/opaque6\n", rewritten);
    }

    [Theory]
    [InlineData("#EXTM3U\nfile:///etc/passwd\n")]
    [InlineData("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=data:secret\n")]
    [InlineData("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"data:secret\"\n")]
    [InlineData("#EXTM3U\n{$host}/segment.ts\n")]
    [InlineData("#EXTM3U\n#EXT-X-DEFINE:NAME=\"host\",VALUE=\"secret\"\n")]
    [InlineData("#EXTM3U\n#EXT-X-CONTENT-STEERING:SERVER-URI=\"https://example.org\"\n")]
    [InlineData("#EXTM3U\nhttps://user:password@example.org/video\n")]
    public void RejectsResourcesThatCannotBeSafelyProxied(string playlist)
    {
        Assert.Throws<InvalidDataException>(() => HlsPlaylistRewriter.Rewrite(playlist, new Uri("https://origin.example/master.m3u8"), _ => "/proxy"));
    }

    [Fact]
    public void PreservesLivePlaylistControlsAndByteRanges()
    {
        const string playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:20\n#EXT-X-BYTERANGE:42@128\n#EXTINF:6,\nchunk.mp4\n#EXT-X-DISCONTINUITY\n#EXT-X-ENDLIST\n";
        var result = HlsPlaylistRewriter.Rewrite(playlist, new Uri("https://origin.example/index"), _ => "/Siphon/media/opaque");
        Assert.Equal(playlist.Replace("chunk.mp4", "/Siphon/media/opaque", StringComparison.Ordinal), result);
    }

    [Fact]
    public void RejectsOversizedPlaylistsBeforeCreatingLeases()
    {
        var invoked = false;
        Assert.Throws<InvalidDataException>(() => HlsPlaylistRewriter.Rewrite("#EXTM3U\n" + new string('x', HlsPlaylistRewriter.MaximumPlaylistBytes), new Uri("https://origin.example/index"), _ =>
        {
            invoked = true;
            return "/proxy";
        }));
        Assert.False(invoked);
    }
}
