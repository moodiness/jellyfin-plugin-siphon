using System.Net;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Downloads;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Playback;
using Jellyfin.Plugin.Siphon.Protocol;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Downloads;

public sealed class DownloadQueueServiceTests
{
    [Fact]
    public async Task MissingSelectionFailsRatherThanSubstitutingAnotherVersionAtExecution()
    {
        await using var fixture = await Fixture.CreateAsync();
        var queued = await fixture.EnqueueAsync();
        fixture.Origin.IncludeSelected = false;
        await fixture.StartAsync();
        await fixture.WaitForAsync(queued.Id, DownloadJobState.Failed);
        Assert.Equal(0, fixture.Transfer.Calls);
        Assert.Contains("exact selected version", fixture.Store.Find(queued.Id, fixture.User.Id)!.Error);
        var ledger = File.ReadAllText(Path.Combine(fixture.Directory, "download-queue.json"));
        Assert.DoesNotContain("private-secret", ledger);
        Assert.DoesNotContain("source-secret", ledger);
        Assert.DoesNotContain("https://", ledger);
    }

    [Fact]
    public async Task CancellationWinsAgainstACompletionAlreadyInFlightAndDoesNotResurrect()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Transfer.IgnoreCancellation = true;
        var queued = await fixture.EnqueueAsync();
        await fixture.StartAsync();
        await fixture.Transfer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Queue.CancelAsync(fixture.User.Id, queued.Id, false, default);
        fixture.Transfer.Release.TrySetResult();
        await fixture.Transfer.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitAsync(() => !System.IO.Directory.Exists(Path.Combine(fixture.Directory, "downloads", queued.Id.ToString("N"))));
        Assert.Equal(DownloadJobState.Cancelled, fixture.Store.Find(queued.Id, fixture.User.Id)!.State);
        await fixture.Queue.StopAsync(default);
        var reopened = new DownloadQueueStore(fixture.Directory);
        reopened.Recover();
        Assert.Null(reopened.TryStart(queued.Id, 2, 1024 * 1024, 1024 * 1024));
        Assert.Equal(DownloadJobState.Cancelled, reopened.Find(queued.Id, fixture.User.Id)!.State);
    }

    [Fact]
    public async Task TransportPolicyChangesCancelAnAlreadyOpenTransfer()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Settings.AllowedPrivateHosts.Add("previously-approved.example");
        var queued = await fixture.EnqueueAsync();
        await fixture.StartAsync();
        await fixture.Transfer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Settings.AllowedPrivateHosts.Clear();
        await fixture.Transfer.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.WaitForAsync(queued.Id, DownloadJobState.Failed);
        Assert.False(fixture.Queue.List(fixture.User.Id).Jobs.Single().CanRetry);
    }

    [Fact]
    public async Task CompletedTicketsRejectForeignUsersAndRevokeOpenReadersAfterPermissionLoss()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Transfer.Release.TrySetResult();
        var queued = await fixture.EnqueueAsync();
        await fixture.StartAsync();
        await fixture.WaitForAsync(queued.Id, DownloadJobState.Completed);
        var ticket = await fixture.Queue.TicketAsync(fixture.User.Id, queued.Id, default);
        Assert.Empty(fixture.Queue.List(Guid.NewGuid()).Jobs);
        Assert.Null(await fixture.Queue.OpenTicketAsync(ticket.Token, Guid.NewGuid(), true, default));
        var opened = await fixture.Queue.OpenTicketAsync(ticket.Token, fixture.User.Id, true, default);
        Assert.NotNull(opened);
        await using var reader = opened.Value.Stream;
        reader.Seek(2, SeekOrigin.Begin);
        var bytes = new byte[2];
        Assert.Equal(2, await reader.ReadAsync(bytes));
        Assert.Equal(new byte[] { 3, 4 }, bytes);
        fixture.User.SetPermission(PermissionKind.EnableContentDownloading, false);
        await Assert.ThrowsAsync<IOException>(async () => await reader.ReadExactlyAsync(bytes));
        Assert.Throws<IOException>(() => reader.Seek(0, SeekOrigin.Begin));
        Assert.Null(await fixture.Queue.OpenTicketAsync(ticket.Token, null, false, default));
        fixture.User.SetPermission(PermissionKind.EnableContentDownloading, true);
        Assert.Null(await fixture.Queue.OpenTicketAsync(ticket.Token, fixture.User.Id, true, default));
    }

    [Fact]
    public async Task ForeignNativeVersionIsRejectedBeforeAddonResolutionEvenForAnAdministrator()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Selected.SetProviderId(NativeVersionService.OwnerProvider, Guid.NewGuid().ToString("N"));
        var requests = fixture.Origin.Requests;
        var error = await Assert.ThrowsAsync<DownloadQueueException>(() => fixture.EnqueueAsync());
        Assert.Equal(404, error.StatusCode);
        Assert.Equal(requests, fixture.Origin.Requests);
        Assert.Empty(fixture.Store.Snapshot());
    }

    [Fact]
    public async Task CancellationDuringSharedAddonResolutionNeverStartsMediaIoWhenMetadataArrives()
    {
        await using var fixture = await Fixture.CreateAsync();
        var queued = await fixture.EnqueueAsync();
        fixture.Origin.HoldStreams = true;
        await fixture.StartAsync();
        await fixture.Origin.StreamsEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Queue.CancelAsync(fixture.User.Id, queued.Id, false, default);
        fixture.Origin.ReleaseStreams.TrySetResult();
        // Prove scheduling continues after late metadata, rather than using host shutdown to suppress the old transfer.
        var fresh = await fixture.EnqueueAsync();
        fixture.Transfer.Release.TrySetResult();
        await fixture.WaitForAsync(fresh.Id, DownloadJobState.Completed);
        Assert.Equal(DownloadJobState.Cancelled, fixture.Store.Find(queued.Id, fixture.User.Id)!.State);
        Assert.Equal(1, fixture.Transfer.Calls);
        Assert.False(System.IO.Directory.Exists(Path.Combine(fixture.Directory, "downloads", queued.Id.ToString("N"))));
    }

    private static async Task WaitAsync(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) await Task.Delay(20, deadline.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = DownloadQueueStoreTests.NewDirectory();
        public User User { get; } = new("viewer", "authentication", "password-reset") { Id = Guid.NewGuid() };
        public PluginConfiguration Settings { get; } = new()
        {
            EnableDownloadQueue = true,
            DownloadMaxStorageMiB = 8,
            DownloadMaxFileMiB = 2,
            Addons = [new() { Id = "fixture", ManifestUrl = "https://addon.example/private-secret/manifest.json", Enabled = true }]
        };
        public Movie Primary { get; } = new() { Id = Guid.NewGuid(), Name = "A legal fixture", VideoType = VideoType.VideoFile };
        public Movie Selected { get; } = new() { Id = Guid.NewGuid(), Name = "A legal fixture", VideoType = VideoType.VideoFile };
        public Origin Origin { get; } = new();
        public ControlledTransfer Transfer { get; } = new();
        public DownloadQueueStore Store { get; private set; } = null!;
        public DownloadQueueService Queue { get; private set; } = null!;
        private StremioClient _client = null!;
        private bool _started;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture.User.SetPermission(PermissionKind.IsAdministrator, true);
            fixture.User.SetPermission(PermissionKind.EnableContentDownloading, true);
            var configuration = new ConfigurationAccessor(() => fixture.Settings);
            fixture._client = new StremioClient(fixture.Origin, configuration);
            var registry = new AddonRegistry(fixture._client, configuration, NullLogger<AddonRegistry>.Instance);
            var resolver = new StreamResolver(fixture._client, registry, configuration, NullLogger<StreamResolver>.Instance,
                new SourceBindingStore(Path.Combine(fixture.Directory, "bindings.json")));
            var state = new State();
            var source = (await resolver.GetSourcesAsync(state.Item, fixture.User.Id, default)).Single(value => value.FileName == "chosen.mp4");
            fixture.Primary.SetProviderId("Siphon", state.Item.Key);
            fixture.Selected.PrimaryVersionId = fixture.Primary.Id;
            fixture.Selected.SetProviderId("Siphon", state.Item.Key);
            fixture.Selected.SetProviderId(NativeVersionService.OwnerProvider, fixture.User.Id.ToString("N"));
            fixture.Selected.SetProviderId(NativeVersionService.SourceProvider, source.Id);
            var users = DispatchProxy.Create<IUserManager, Boundary>();
            ((Boundary)(object)users).Call = (method, arguments) => method.Name == "GetUserById"
                ? arguments![0] is Guid id && id == fixture.User.Id ? fixture.User : null : throw new NotSupportedException();
            var authorization = DispatchProxy.Create<IAuthorizationContext, Boundary>();
            ((Boundary)(object)authorization).Call = (_, _) => Task.FromResult(new AuthorizationInfo { User = fixture.User });
            var library = DispatchProxy.Create<ILibraryManager, Boundary>();
            ((Boundary)(object)library).Call = (method, arguments) => method.Name switch
            {
                "GetItemById" => arguments![0] is Guid id ? id == fixture.Primary.Id ? fixture.Primary : id == fixture.Selected.Id ? fixture.Selected : null : null,
                "GetItemList" => new BaseItem[] { fixture.Primary, fixture.Selected },
                "ConfigureUserAccess" => null,
                _ => throw new NotSupportedException(method.Name)
            };
            var access = new PlaybackAccess(new HttpContextAccessor(), authorization, users, library);
            fixture.Store = new DownloadQueueStore(fixture.Directory);
            fixture.Queue = new DownloadQueueService(fixture.Store, fixture.Transfer, configuration, access, users, state, resolver,
                new StartedLifetime(), NullLogger<DownloadQueueService>.Instance);
            return fixture;
        }

        public Task<DownloadJobSummary> EnqueueAsync()
            => Queue.EnqueueAsync(User.Id, new(Primary.Id, Selected.Id.ToString("N")), default);
        public Task WaitForAsync(Guid id, DownloadJobState state) => WaitAsync(() => Store.Find(id, User.Id)?.State == state);
        public async Task StartAsync() { _started = true; await Queue.StartAsync(default); }
        public async ValueTask DisposeAsync()
        {
            Origin.ReleaseStreams.TrySetResult();
            Transfer.Release.TrySetResult();
            if (_started) await Queue.StopAsync(default);
            Queue.Dispose();
            _client.Dispose();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    public class Boundary : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
    }

    private sealed class StartedLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => new(canceled: true);
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class State : ISiphonStateStore
    {
        public ManagedItem Item { get; } = new() { Key = "movie:fixture", Type = "movie", ContentId = "fixture", ContentKey = "movie:fixture", VideoId = "fixture", Name = "A legal fixture", Path = "fixture.strm" };
        public IReadOnlyList<ManagedItem> GetItems() => [Item];
        public ManagedItem? FindByKey(string key) => key == Item.Key ? Item : null;
        public ManagedItem? FindByPath(string path) => path == Item.Path ? Item : null;
        public ManagedItem? FindByContentKey(string contentKey) => contentKey == Item.ContentKey ? Item : null;
        public Task SaveAsync(IReadOnlyList<ManagedItem> items, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Origin : ISafeHttpClient
    {
        public bool IncludeSelected { get; set; } = true;
        public int Requests { get; private set; }
        public bool HoldStreams { get; set; }
        public TaskCompletionSource StreamsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStreams { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            Requests++;
            if (HoldStreams && !uri.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal))
            {
                StreamsEntered.TrySetResult();
                await ReleaseStreams.Task;
            }
            var body = uri.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal)
                ? """{"id":"fixture","resources":["stream"],"types":["movie"]}"""
                : JsonSerializer.Serialize(new
                {
                    streams = (IncludeSelected ? new[] { "chosen", "other" } : new[] { "other" }).Select(name => new
                    {
                        name,
                        url = "https://media.example/" + name + ".mp4?token=source-secret",
                        behaviorHints = new { filename = name + ".mp4" }
                    }).ToArray()
                });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    private sealed class ControlledTransfer : IDownloadTransfer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IgnoreCancellation { get; set; }
        public int Calls { get; private set; }
        public async Task<DownloadTransferResult> TransferAsync(ResolvedStream source, string directory, long maximumBytes,
            Func<DownloadTransferProgress, Task> progress, CancellationToken cancellationToken)
        {
            Calls++;
            Started.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken);
                await File.WriteAllBytesAsync(Path.Combine(directory, "video.mp4"), [1, 2, 3, 4], CancellationToken.None);
                return new("video.mp4", "video/mp4", 4);
            }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            finally { Finished.TrySetResult(); }
        }
    }
}
