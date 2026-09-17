using System.Reflection;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Siphon.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Identity;

public sealed class FollowedSeriesSelectorTests
{
    [Fact]
    public async Task NativeStateAggregatesUsersVersionsAndAncestorFavoritesWithoutFollowingUnstartedShows()
    {
        using var database = new Database();
        var first = Episode("first");
        var second = Episode("second");
        var third = Episode("third");
        var fourth = Episode("fourth");
        var fifth = Episode("fifth");
        var sixth = Episode("sixth");
        var untouched = Episode("untouched");
        await database.Seed(first.Key, played: true, alternate: true);
        await database.Seed(second.ContentKey, favorite: true);
        await database.Seed(third.ContentKey + ":season:1", favorite: true);
        await database.Seed(fourth.Key, resume: 100);
        await database.Seed(fifth.Key, playCount: 1);
        await database.Seed(sixth.Key, favorite: true);
        await database.Seed(untouched.ContentKey, played: true);
        await database.Seed("series:unmanaged:1:1", favorite: true);
        var selected = await new FollowedSeriesSelector(database).SelectAsync(
            [first, second, third, fourth, fifth, sixth, untouched], CancellationToken.None);
        Assert.Equal(new[] { first, second, third, fourth, fifth, sixth }.Select(item => item.ContentKey).Order(StringComparer.Ordinal),
            selected.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task QueryFailureIsNotAnEmptyFollowedSelection()
    {
        await Assert.ThrowsAsync<IOException>(() => new FollowedSeriesSelector(new UnavailableDatabase())
            .SelectAsync([Episode("first")], CancellationToken.None));
    }

    internal static ManagedItem Episode(string id) => new()
    {
        Key = "series:" + id + ":1:1",
        ContentKey = "series:" + id,
        Type = "series",
        ContentId = id,
        VideoId = id + ":video:1",
        Name = "Episode",
        SeriesName = "Series",
        Season = 1,
        Episode = 1,
        Path = "/siphon/" + id,
        Owners = ["installation:subscription"],
        StreamIdentities = [new("series", id + ":video:1")]
    };

    private sealed class UnavailableDatabase : IDbContextFactory<JellyfinDbContext>
    {
        public JellyfinDbContext CreateDbContext() => throw new IOException("Database unavailable.");
    }

    internal sealed class Database : IDbContextFactory<JellyfinDbContext>, IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly DbContextOptions<JellyfinDbContext> _options;
        private readonly IJellyfinDatabaseProvider _provider = DispatchProxy.Create<IJellyfinDatabaseProvider, NativeCreditsTests.DatabaseHooks>();
        private readonly IEntityFrameworkCoreLockingBehavior _locking = DispatchProxy.Create<IEntityFrameworkCoreLockingBehavior, NativeCreditsTests.DatabaseHooks>();

        public Database()
        {
            _connection.Open();
            _options = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection).Options;
            using var context = CreateDbContext();
            context.Database.EnsureCreated();
        }

        public JellyfinDbContext CreateDbContext() => new(_options, NullLogger<JellyfinDbContext>.Instance, _provider, _locking);

        public async Task Seed(string key, bool favorite = false, bool played = false, long resume = 0, int playCount = 0, bool alternate = false)
        {
            await using var context = CreateDbContext();
            var item = new BaseItemEntity { Id = Guid.NewGuid(), Name = "Native item", Type = "MediaBrowser.Controller.Entities.TV.Episode" };
            item.Provider = [new BaseItemProvider { Item = item, ItemId = item.Id, ProviderId = "Siphon", ProviderValue = key }];
            if (alternate) item.Provider.Add(new BaseItemProvider
            {
                Item = item,
                ItemId = item.Id,
                ProviderId = "SiphonVersion",
                ProviderValue = Guid.NewGuid().ToString("N")
            });
            var user = new User(Guid.NewGuid().ToString("N"), "authentication", "password-reset");
            context.Add(user);
            context.Add(item);
            context.UserData.Add(new UserData
            {
                Item = item,
                ItemId = item.Id,
                User = user,
                UserId = user.Id,
                CustomDataKey = string.Empty,
                IsFavorite = favorite,
                Played = played,
                PlaybackPositionTicks = resume,
                PlayCount = playCount
            });
            await context.SaveChangesAsync();
        }

        public void Dispose() => _connection.Dispose();
    }
}
