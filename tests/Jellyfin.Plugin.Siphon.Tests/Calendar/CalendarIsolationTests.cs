using System.Reflection;
using Jellyfin.Plugin.Siphon.Calendar;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Tests.Identity;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Calendar;

public sealed class CalendarIsolationTests
{
    [Fact]
    public async Task FollowingUsesOnlyRequestedUsersNativeHistoryIncludingAlternateVersions()
    {
        using var database = new FollowedSeriesSelectorTests.Database();
        var first = FollowedSeriesSelectorTests.Episode("alice");
        var second = FollowedSeriesSelectorTests.Episode("bob");
        await database.Seed(first.Key, resume: 100, alternate: true);
        await database.Seed(second.ContentKey, favorite: true);
        await using var context = database.CreateDbContext();
        var alice = await context.UserData.Where(data => data.Item!.Provider!.Any(provider => provider.ProviderValue == first.Key))
            .Select(data => data.UserId).SingleAsync();
        var bob = await context.UserData.Where(data => data.Item!.Provider!.Any(provider => provider.ProviderValue == second.ContentKey))
            .Select(data => data.UserId).SingleAsync();
        var calendar = new FollowedCalendarService(null!, null!, database);
        var managed = new[] { ManagedItemSnapshot.CopyOf(first), ManagedItemSnapshot.CopyOf(second) };
        Assert.Equal(first.ContentKey, Assert.Single(await calendar.SelectFollowedAsync(alice, managed, CancellationToken.None)));
        Assert.Equal(second.ContentKey, Assert.Single(await calendar.SelectFollowedAsync(bob, managed, CancellationToken.None)));
        Assert.Empty(await calendar.SelectFollowedAsync(Guid.NewGuid(), managed, CancellationToken.None));
    }

    [Fact]
    public async Task NativeMessagesNeverTargetOtherUsersSharedSessionsOrInactiveControllers()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await using var session = new SessionInfo(null!, NullLogger.Instance)
        {
            UserId = alice,
            Capabilities = new ClientCapabilities { SupportsMediaControl = true, SupportedCommands = [GeneralCommandType.DisplayMessage] },
            SessionControllers = [new Controller(true)]
        };
        Assert.True(CalendarNotificationService.CanSend(session, alice));
        Assert.False(CalendarNotificationService.CanSend(session, bob));
        session.Capabilities.SupportedCommands = [];
        Assert.False(CalendarNotificationService.CanSend(session, alice));
        session.Capabilities.SupportedCommands = [GeneralCommandType.DisplayMessage];
        session.AdditionalUsers = [new SessionUserInfo { UserId = bob }];
        Assert.False(CalendarNotificationService.CanSend(session, alice));
        session.AdditionalUsers = [];
        session.SessionControllers = [new Controller(false)];
        Assert.False(CalendarNotificationService.CanSend(session, alice));
    }

    [Fact]
    public async Task BriefPublicationContentionDoesNotDropCommittedEpisodeNotification()
    {
        var directory = Path.Combine(Path.GetTempPath(), "siphon-calendar-contention-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var database = new FollowedSeriesSelectorTests.Database();
            var first = FollowedSeriesSelectorTests.Episode("contention");
            var second = first with
            {
                Key = first.ContentKey + ":1:2",
                VideoId = first.ContentId + ":video:2",
                Episode = 2,
                Path = first.Path + "-2",
                StreamIdentities = [new("series", first.ContentId + ":video:2")]
            };
            await database.Seed(first.ContentKey, favorite: true);
            await using var context = database.CreateDbContext();
            var user = await context.Users.SingleAsync();
            var identities = new Dictionary<string, Guid>
            {
                ["siphon:media:" + first.Key] = Guid.NewGuid(),
                ["siphon:media:" + second.Key] = Guid.NewGuid(),
                ["siphon:series:" + first.ContentKey] = Guid.NewGuid()
            };
            Episode Published(ManagedItem item) => new()
            {
                Id = identities["siphon:media:" + item.Key],
                Name = item.Name,
                SeriesName = item.SeriesName,
                ParentIndexNumber = item.Season,
                IndexNumber = item.Episode,
                PremiereDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ProviderIds = new Dictionary<string, string> { ["Siphon"] = item.Key }
            };
            var native = new List<BaseItem> { Published(first) };
            var library = DispatchProxy.Create<ILibraryManager, NativeBoundary>();
            ((NativeBoundary)(object)library).Call = (method, args) => method.Name switch
            {
                "GetNewItemId" => identities[(string)args![0]!],
                "ConfigureUserAccess" => null,
                "GetItemList" => native.Where(item => ((InternalItemsQuery)args![0]!).ItemIds.Contains(item.Id)).ToList(),
                _ => throw new NotSupportedException(method.Name)
            };
            var users = DispatchProxy.Create<IUserManager, NativeBoundary>();
            ((NativeBoundary)(object)users).Call = (method, _) => method.Name switch
            {
                "GetUsers" => new[] { user },
                "GetUserById" => user,
                _ => throw new NotSupportedException(method.Name)
            };
            var sessions = DispatchProxy.Create<ISessionManager, NativeBoundary>();
            ((NativeBoundary)(object)sessions).Call = (method, _) => method.Name == "get_Sessions"
                ? Array.Empty<SessionInfo>() : throw new NotSupportedException(method.Name);
            var paths = DispatchProxy.Create<IApplicationPaths, NativeBoundary>();
            ((NativeBoundary)(object)paths).Call = (method, _) => method.Name == "get_DataPath"
                ? directory : throw new NotSupportedException(method.Name);
            var siphonPaths = new SiphonPaths(paths);
            var settings = new PluginConfiguration { EnableCalendarNotifications = true };
            var configuration = new ConfigurationAccessor(() => settings);
            using var state = new SiphonStateStore(siphonPaths);
            using var preferences = new UserPreferenceStore(siphonPaths, configuration);
            using var inbox = new CalendarNotificationStore(siphonPaths);
            var calendar = new FollowedCalendarService(state, library, database);
            using var observer = new CalendarNotificationService(configuration, preferences, state, calendar, inbox,
                users, sessions, NullLogger<CalendarNotificationService>.Instance);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await preferences.SaveAsync(user.Id, new() { NotificationsEnabled = true }, cancellation.Token);
            await state.SaveAsync([first], cancellation.Token);
            await observer.ObserveAsync(cancellation.Token);
            Assert.Empty(inbox.Get(user.Id));

            Task observation;
            await configuration.SynchronizationGate.WaitAsync(cancellation.Token);
            try
            {
                await state.SaveAsync([first, second], cancellation.Token);
                native.Add(Published(second));
                // The baseline is unchanged, so the cycle reaches the held publication
                // gate synchronously. Release it without timers or timing assumptions.
                observation = observer.ObserveAsync(cancellation.Token);
            }
            finally { configuration.SynchronizationGate.Release(); }
            await observation;
            var notification = Assert.Single(inbox.Get(user.Id));
            Assert.Equal("NewEpisode", notification.Kind);
            Assert.Equal(identities["siphon:media:" + second.Key], notification.Episode.Id);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    public class NativeBoundary : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
    }

    private sealed class Controller(bool active) : ISessionController
    {
        public bool IsSessionActive => active;
        public bool SupportsMediaControl => true;
        public Task SendMessage<T>(SessionMessageType name, Guid messageId, T data, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
