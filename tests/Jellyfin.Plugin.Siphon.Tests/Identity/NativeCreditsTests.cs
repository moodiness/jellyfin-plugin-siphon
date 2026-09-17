using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Identity;

public sealed class NativeCreditsTests
{
    [Fact]
    public async Task UnicodeCreditsReuseIdentityAcrossItemsAndRepeatedRoleChanges()
    {
        using var database = new Database();
        var first = new Movie { Id = Guid.NewGuid(), Name = "First" };
        var second = new Movie { Id = Guid.NewGuid(), Name = "Second" };
        await database.Seed(first, second);
        var actorId = Guid.NewGuid();
        await using (var context = database.CreateDbContext())
        {
            context.Peoples.Add(new People { Id = actorId, Name = "Élodie", PersonType = "Actor" });
            context.PeopleBaseItemMap.Add(new PeopleBaseItemMap
            {
                ItemId = first.Id,
                Item = null!,
                PeopleId = actorId,
                People = null!,
                Role = "Original",
                ListOrder = 0
            });
            await context.SaveChangesAsync();
        }
        var mapper = Mapper(database);
        var managed = Item("first") with { People = [new("éLODIE", "Actor", "Updated")] };
        await mapper.SavePeopleAsync([(first, managed, false), (second, managed with { Key = "second" }, true)], CancellationToken.None);
        await mapper.SavePeopleAsync([(first, managed, false), (second, managed with { Key = "second" }, false)], CancellationToken.None);
        await using var result = database.CreateDbContext();
        Assert.Equal(actorId, (await result.Peoples.SingleAsync()).Id);
        var credits = await result.PeopleBaseItemMap.OrderBy(map => map.ItemId).ToArrayAsync();
        Assert.Equal(2, credits.Length);
        Assert.All(credits, credit => { Assert.Equal(actorId, credit.PeopleId); Assert.Equal("Updated", credit.Role); });
    }

    [Fact]
    public async Task CancellationStopsAtACommittedBatchAndRetryRetainsCredits()
    {
        using var database = new Database();
        var movies = Enumerable.Range(0, 260).Select(index => new Movie { Id = Guid.NewGuid(), Name = "Movie " + index }).ToArray();
        await database.Seed(movies);
        var entries = movies.Select((movie, index) => ((BaseItem)movie, Item(index.ToString()), true)).ToArray();
        using var cancellation = new CancellationTokenSource();
        var mapper = Mapper(database);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mapper.SavePeopleAsync(entries, cancellation.Token,
            new CallbackProgress(_ => cancellation.Cancel())));
        Guid actorId;
        await using (var interrupted = database.CreateDbContext())
        {
            actorId = (await interrupted.Peoples.SingleAsync()).Id;
            var committed = await interrupted.PeopleBaseItemMap.CountAsync();
            Assert.InRange(committed, 1, movies.Length - 1);
        }
        await mapper.SavePeopleAsync(entries, CancellationToken.None);
        await using var result = database.CreateDbContext();
        Assert.Equal(movies.Length, await result.PeopleBaseItemMap.CountAsync());
        Assert.Equal(actorId, (await result.Peoples.SingleAsync()).Id);
        Assert.All(await result.PeopleBaseItemMap.ToArrayAsync(), credit => Assert.Equal(actorId, credit.PeopleId));
    }

    private static ManagedItem Item(string key) => new()
    {
        Key = key,
        ContentKey = "movie:" + key,
        ContentId = key,
        VideoId = key,
        Type = "movie",
        Name = key,
        Path = "/library/" + key,
        People = [new("Élodie", "Actor", "Role")]
    };

    private static ItemMetadataMapper Mapper(Database database)
        => new(new ConfigurationAccessor(() => new PluginConfiguration { MetadataUpdateMode = "RefreshSelected", MetadataRefreshFields = ["People"] }),
            null!, DispatchProxy.Create<ILibraryManager, Library>(), database, DispatchProxy.Create<IItemPersistenceService, CatalogSyncTests.VersionPersistenceProxy>());

    private sealed class CallbackProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    public class Library : DispatchProxy
    {
        private readonly Dictionary<Guid, Person> _people = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "GetPersonId")
            {
                var name = (string)args![0]!;
                var id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant())));
                _people.TryAdd(id, new Person { Id = id, Name = name, DateLastSaved = DateTime.UtcNow });
                return id;
            }
            if (method.Name == "GetItemList") return ((InternalItemsQuery)args![0]!).ItemIds.Select(id => (BaseItem)_people[id]).ToArray();
            throw new InvalidOperationException("Unexpected native operation: " + method.Name);
        }
    }

    public class DatabaseHooks : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (args?.OfType<Func<Task>>().FirstOrDefault() is { } asynchronous) return asynchronous();
            if (args?.OfType<Action>().FirstOrDefault() is { } synchronous) { synchronous(); return null; }
            if (method!.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (method.ReturnType == typeof(void)) return null;
            if (method.ReturnType.IsValueType) return Activator.CreateInstance(method.ReturnType);
            throw new InvalidOperationException("Unhandled database hook: " + method.Name + " -> " + method.ReturnType);
        }
    }

    private sealed class Database : IDbContextFactory<JellyfinDbContext>, IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly DbContextOptions<JellyfinDbContext> _options;
        private readonly IJellyfinDatabaseProvider _provider = DispatchProxy.Create<IJellyfinDatabaseProvider, DatabaseHooks>();
        private readonly IEntityFrameworkCoreLockingBehavior _locking = DispatchProxy.Create<IEntityFrameworkCoreLockingBehavior, DatabaseHooks>();

        public Database()
        {
            _connection.Open();
            _options = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection).Options;
            using var context = CreateDbContext();
            context.Database.EnsureCreated();
        }

        public JellyfinDbContext CreateDbContext() => new(_options, NullLogger<JellyfinDbContext>.Instance, _provider, _locking);

        public async Task Seed(params Movie[] movies)
        {
            await using var context = CreateDbContext();
            foreach (var movie in movies) context.BaseItems.Add(new BaseItemEntity { Id = movie.Id, Name = movie.Name, Type = typeof(Movie).FullName! });
            await context.SaveChangesAsync();
        }

        public void Dispose() => _connection.Dispose();
    }
}
