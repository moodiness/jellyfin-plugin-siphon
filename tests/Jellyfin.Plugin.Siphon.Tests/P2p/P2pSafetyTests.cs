using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.P2p;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MonoTorrent;
using MonoTorrent.BEncoding;
using Xunit;
namespace Jellyfin.Plugin.Siphon.Tests.P2p;

public sealed class P2pSafetyTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void MagnetHashConflictIsRejectedRatherThanChoosingAnotherTorrent()
    {
        Assert.Throws<ArgumentException>(() => P2pSource.Parse(Hash, null, null, null,
            "magnet:?xt=urn:btih:ffffffffffffffffffffffffffffffffffffffff"));
        var hybrid = P2pSource.Parse(Hash, 1, null, ["tracker:https://tracker.example/announce?passkey=private"],
            "magnet:?xt=urn%3Abtih%3A" + Hash + "&xt=urn%3Abtmh%3A1220" + new string('a', 64));
        Assert.Equal(Hash, hybrid!.ToMagnet().InfoHashes.V1!.ToHex().ToLowerInvariant());
        Assert.Equal(new string('a', 64), hybrid.ToMagnet().InfoHashes.V2!.ToHex().ToLowerInvariant());
    }

    [Fact]
    public void Base32AndHexIdentifyTheSameSwarm()
    {
        var base32 = P2pSource.Parse(new string('A', 32), null, null, null, null)!;
        var hex = P2pSource.Parse(new string('0', 40), null, null, null, null)!;
        Assert.Equal(hex.InfoHash, base32.InfoHash);
        Assert.Throws<ArgumentException>(() => P2pSource.Parse(new string('0', 32), null, null, null, null));
    }

    [Fact]
    public void MultipleVideosRequireAnUnambiguousSelection()
    {
        (string Path, long Length)[] files = [("season/show_S01E01.mkv", 100), ("season/show_S01E02.mkv", 1000), ("README.txt", 10)];
        var source = new P2pSource(Hash, null, null, []);
        Assert.Throws<InvalidOperationException>(() => P2pStorage.SelectFile(files, source));
        Assert.Equal(0, P2pStorage.SelectFile(files, source with { Season = 1, Episode = 1 }));
        Assert.Equal(1, P2pStorage.SelectFile(files, source with { FileIndex = 1 }));
        Assert.Throws<InvalidOperationException>(() => P2pStorage.SelectFile(files, source with { FileIndex = 2 }));
        Assert.Throws<InvalidOperationException>(() => P2pStorage.SelectFile(files, source with { FileName = "missing.mkv" }));
        Assert.Throws<InvalidOperationException>(() => P2pStorage.SelectFile([("a/movie.mkv", 1), ("b/movie.mkv", 2)], source with { FileName = "movie.mkv" }));
    }

    [Fact]
    public void FragmentedPeerHandshakeCannotAdvertiseUnboundedMetadata()
    {
        var valid = PeerFrame("d13:metadata_sizei16384ee");
        var oversized = PeerFrame("d13:metadata_sizei2147483647ee");
        var guard = new PeerWireGuard();
        guard.Accept(new byte[68]);
        foreach (var value in valid) guard.Accept([value]);
        // A second extension handshake is allowed, but cannot increase the allocation beyond the bound.
        guard.Accept(oversized.AsSpan(0, oversized.Length - 1));
        Assert.Throws<IOException>(() => guard.Accept(oversized.AsSpan(oversized.Length - 1)));
        var nesting = PeerFrame(new string('l', 33) + new string('e', 33));
        var nestedGuard = new PeerWireGuard();
        nestedGuard.Accept(new byte[68]);
        Assert.Throws<IOException>(() => nestedGuard.Accept(nesting));
    }

    private static byte[] PeerFrame(string encoded)
    {
        var bytes = Encoding.ASCII.GetBytes(encoded);
        var frame = new byte[bytes.Length + 6];
        BinaryPrimitives.WriteInt32BigEndian(frame, bytes.Length + 2);
        frame[4] = 20;
        bytes.CopyTo(frame, 6);
        return frame;
    }

    [Fact]
    public void FileIndexAccountsForPaddingEntriesRemovedByTheEngine()
    {
        static BEncodedDictionary Entry(string path, long length) => new()
        {
            ["path"] = new BEncodedList { new BEncodedString(path) },
            ["length"] = new BEncodedNumber(length)
        };
        var padding = Entry(".pad/16383", 16383);
        padding["attr"] = new BEncodedString("p");
        var metadata = new BEncodedDictionary
        {
            ["info"] = new BEncodedDictionary
            {
                ["name"] = new BEncodedString("legal-fixture"),
                ["piece length"] = new BEncodedNumber(16384),
                ["pieces"] = new BEncodedString(new byte[40]),
                ["files"] = new BEncodedList { Entry("first.mkv", 1), padding, Entry("second.mkv", 1) }
            }
        };
        var bytes = metadata.Encode();
        var torrent = Torrent.Load(bytes);
        Assert.Equal("second.mkv", torrent.Files[P2pStorage.MapFileIndex(torrent, bytes, 2)].Path);
        Assert.Throws<InvalidOperationException>(() => P2pStorage.MapFileIndex(torrent, bytes, 1));
    }

    [Theory]
    [InlineData("../outside.mkv")]
    [InlineData("a/../../outside.mkv")]
    [InlineData("/outside.mkv")]
    [InlineData("C:\\outside.mkv")]
    [InlineData("movie.mkv:stream")]
    public void TorrentPathsCannotEscapeTheirReservation(string value)
        => Assert.Throws<IOException>(() => P2pStorage.ValidateRelativePath(value));

    [Fact]
    public async Task DisabledPlaybackDoesNotCreateEngineStorageOrResolveItsTracker()
    {
        var directory = NewDirectory();
        var configuration = new ConfigurationAccessor(() => new PluginConfiguration { EnableP2p = false });
        using var service = Service(configuration, directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenAsync(new P2pSource(Hash, null, null, ["http://127.0.0.1:1/announce"]), Guid.NewGuid(), CancellationToken.None));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task ForbiddenPrivateTrackerFailsBeforeAnEngineCanContactItAndReleasesTheSession()
    {
        var directory = NewDirectory();
        try
        {
            var configuration = new ConfigurationAccessor(() => new PluginConfiguration { EnableP2p = true });
            using var service = Service(configuration, directory);
            await Assert.ThrowsAsync<HttpRequestException>(() => service.OpenAsync(new P2pSource(Hash, null, null, ["http://127.0.0.1:1/announce"]), Guid.NewGuid(), CancellationToken.None));
            Assert.Equal(0, service.GetStatus().ActiveStreams);
            Assert.Equal(0, service.GetStatus().ReservedBytes);
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(directory, "siphon", "p2p")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CleanupPreservesUnknownDirectoriesAndRefusesLinkedExternalFiles()
    {
        var directory = NewDirectory();
        try
        {
            var configuration = new ConfigurationAccessor(() => new PluginConfiguration());
            using var service = Service(configuration, directory);
            var root = Path.Combine(directory, "siphon", "p2p");
            var unrelated = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "personal.txt"), "keep");
            var owned = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(owned);
            File.Create(Path.Combine(owned, ".siphon-p2p-v1")).Dispose();
            File.WriteAllText(Path.Combine(owned, "cache"), "discard");
            Assert.Equal(1, service.Cleanup().RemovedDirectories);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(unrelated, "personal.txt")));
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(owned);
                File.Create(Path.Combine(owned, ".siphon-p2p-v1")).Dispose();
                Directory.CreateSymbolicLink(Path.Combine(owned, "escape"), unrelated);
                Assert.Throws<IOException>(() => service.Cleanup());
                Assert.Equal("keep", File.ReadAllText(Path.Combine(unrelated, "personal.txt")));
                Directory.Delete(Path.Combine(owned, "escape"));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static string NewDirectory()
    {
        var root = Path.GetTempPath();
        for (var cursor = new DirectoryInfo(root); cursor is not null; cursor = cursor.Parent)
        {
            if (cursor.ResolveLinkTarget(true) is { } target)
                root = Path.Combine(target.FullName, Path.GetRelativePath(cursor.FullName, root));
        }
        return Path.Combine(root, "siphon-p2p-tests-" + Guid.NewGuid().ToString("N"));
    }

    private static P2pStreamService Service(ConfigurationAccessor configuration, string directory)
    {
        var paths = DispatchProxy.Create<IApplicationPaths, PathsProxy>();
        ((PathsProxy)(object)paths).Directory = directory;
        return new P2pStreamService(configuration, new SiphonPaths(paths), new SsrfPolicy(configuration), NullLogger<P2pStreamService>.Instance);
    }

    public class PathsProxy : DispatchProxy
    {
        public string Directory { get; set; } = string.Empty;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_DataPath" ? Directory : throw new NotSupportedException();
    }
}
