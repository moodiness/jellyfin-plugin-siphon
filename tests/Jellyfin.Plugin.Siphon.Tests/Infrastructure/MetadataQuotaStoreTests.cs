using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Metadata;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Infrastructure;

public sealed class MetadataQuotaStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "siphon-quotas-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly Warnings _warnings = new();
    private string QuotaPath => Path.Combine(Paths().DataDirectory, "metadata-quotas.json");

    [Fact]
    public void ExpiredCredentialHistoryCannotMakeActiveDeadlinesUnreadableAfterRestart()
    {
        var observed = _clock.GetUtcNow().AddDays(-30);
        var current = new MetadataQuotaState(Limit: 100, Remaining: 7,
            ResetUtc: observed.AddDays(-1), ObservedAtUtc: observed.AddDays(-2));
        var until = _clock.GetUtcNow().AddHours(4);
        var history = new Dictionary<string, object>(StringComparer.Ordinal);
        for (var index = 0; index < 5000; index++)
            history["Tmdb:" + Credential(index)] = new { Remaining = 2, ResetUtc = observed.AddHours(1), ObservedAtUtc = observed };
        history["Tmdb:" + Credential(6000)] = new MetadataQuotaState(until);
        history["Tmdb:" + Credential(6001)] = current;
        history["MdbList:" + Credential(6002)] = current;
        Directory.CreateDirectory(Paths().DataDirectory);
        File.WriteAllText(QuotaPath, JsonSerializer.Serialize(history));
        Assert.True(new FileInfo(QuotaPath).Length <= 1024 * 1024);

        var store = Store();
        Assert.Equal(current, store.Get("Tmdb", Credential(6001)));
        store.Record("Fanart", Credential(6003), new MetadataQuotaState(until.AddHours(1)));
        Assert.True(new FileInfo(QuotaPath).Length <= 1024 * 1024);

        var restarted = Store();
        Assert.Equal(until, restarted.Get("Tmdb", Credential(6000)).PausedUntilUtc);
        Assert.Equal(current, restarted.Get("Tmdb", Credential(6001)));
        // Compaction for one provider must not drop another provider's as-yet unaccessed credential.
        Assert.Equal(current, restarted.Get("MdbList", Credential(6002)));
        Assert.Equal(until.AddHours(1), restarted.Get("Fanart", Credential(6003)).PausedUntilUtc);
        Assert.Empty(_warnings.Messages);
    }

    [Fact]
    public void UnchangedRecordsRetryFailedWritesWithoutHammeringTheFilesystem()
    {
        Directory.CreateDirectory(QuotaPath);
        var store = Store();
        var state = new MetadataQuotaState(_clock.GetUtcNow().AddHours(1), 100, 0,
            _clock.GetUtcNow().AddHours(1), _clock.GetUtcNow());
        store.Record("Tmdb", Credential(1), state);
        Assert.Single(_warnings.Messages);
        for (var index = 0; index < 10; index++)
        {
            Assert.Equal(state, store.Get("Tmdb", Credential(1)));
            Assert.Equal(state, store.Record("Tmdb", Credential(1), state));
        }
        _clock.Advance(TimeSpan.FromSeconds(29));
        store.Record("Tmdb", Credential(1), state);
        Assert.Single(_warnings.Messages);
        _clock.Advance(TimeSpan.FromSeconds(1));
        store.Record("Tmdb", Credential(1), state);
        Assert.Equal(2, _warnings.Messages.Count);

        Directory.Delete(QuotaPath);
        _clock.Advance(TimeSpan.FromSeconds(30));
        store.Record("Tmdb", Credential(1), state);
        Assert.Equal(state, Store().Get("Tmdb", Credential(1)));
        Assert.Equal(2, _warnings.Messages.Count);
        Assert.Empty(Directory.GetFiles(Paths().DataDirectory, "*.tmp"));

        File.Move(QuotaPath, QuotaPath + ".saved");
        Directory.CreateDirectory(QuotaPath);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(state, store.Get("Tmdb", Credential(1)));
        store.Record("Tmdb", Credential(1), state);
        Assert.Equal(2, _warnings.Messages.Count);
    }

    [Fact]
    public void OrdinaryStatusReadPersistsPendingDeadlineAfterFilesystemRecoveryWithoutNetwork()
    {
        var config = new PluginConfiguration { EnableTmdbMetadata = true, TmdbReadAccessToken = "synthetic-private-token" };
        var credential = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { config.TmdbReadAccessToken, "" }))));
        Directory.CreateDirectory(QuotaPath);
        var store = Store();
        using var client = new MetadataProviderClient(new(() => config), new NoNetwork(), null!, timeProvider: _clock, quotas: store);
        Assert.Null(client.GetStatus().Single(status => status.Provider == "Tmdb").PausedUntilUtc);
        var until = _clock.GetUtcNow().AddHours(2);
        store.Record("Tmdb", credential, new MetadataQuotaState(until));
        Assert.Single(_warnings.Messages);
        Assert.Equal(until, client.GetStatus().Single(status => status.Provider == "Tmdb").PausedUntilUtc);

        Directory.Delete(QuotaPath);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(until, client.GetStatus().Single(status => status.Provider == "Tmdb").PausedUntilUtc);
        Assert.Equal(until, Store().Get("Tmdb", credential).PausedUntilUtc);
        Assert.Single(_warnings.Messages);
        Assert.DoesNotContain(config.TmdbReadAccessToken, File.ReadAllText(QuotaPath));
    }

    [Fact]
    public void HardDeadlinesAloneExceedingWriteBoundDoNotReplaceTheReadableDurableFile()
    {
        var until = _clock.GetUtcNow().AddHours(1);
        var saved = Enumerable.Range(0, 6000).ToDictionary(index => "Tmdb:" + Credential(index), _ => until);
        Directory.CreateDirectory(Paths().DataDirectory);
        var previous = JsonSerializer.Serialize(saved);
        File.WriteAllText(QuotaPath, previous);
        Assert.True(new FileInfo(QuotaPath).Length <= 1024 * 1024);

        var store = Store();
        store.Get("Tmdb", Credential(0));
        store.Record("Tmdb", Credential(6000), new MetadataQuotaState(until.AddHours(1)));
        Assert.Single(_warnings.Messages);
        Assert.Equal(previous, File.ReadAllText(QuotaPath));
        Assert.Equal(until.AddHours(1), store.Get("Tmdb", Credential(6000)).PausedUntilUtc);
        Assert.Equal(until, store.Get("Tmdb", Credential(5999)).PausedUntilUtc);
        Assert.Equal(until, Store().Get("Tmdb", Credential(5999)).PausedUntilUtc);
        Assert.Empty(Directory.GetFiles(Paths().DataDirectory, "*.tmp"));
    }

    private MetadataQuotaStore Store() => new(Paths(), _warnings, _clock);
    private static string Credential(int value) => value.ToString("X64", CultureInfo.InvariantCulture);
    private SiphonPaths Paths()
    {
        var paths = DispatchProxy.Create<IApplicationPaths, PathsProxy>();
        ((PathsProxy)(object)paths).Directory = _directory;
        return new(paths);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class Warnings : ILogger<MetadataQuotaStore>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Messages.Add(formatter(state, exception));
        }
    }
    private sealed class NoNetwork : ISafeHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Quota status must not contact providers.");
    }
    public class PathsProxy : DispatchProxy
    {
        public string Directory { get; set; } = string.Empty;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name == "get_DataPath" ? Directory : throw new NotSupportedException();
    }
}
