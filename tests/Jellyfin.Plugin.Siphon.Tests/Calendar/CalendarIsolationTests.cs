using Jellyfin.Plugin.Siphon.Calendar;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Tests.Identity;
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

    private sealed class Controller(bool active) : ISessionController
    {
        public bool IsSessionActive => active;
        public bool SupportsMediaControl => true;
        public Task SendMessage<T>(SessionMessageType name, Guid messageId, T data, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
