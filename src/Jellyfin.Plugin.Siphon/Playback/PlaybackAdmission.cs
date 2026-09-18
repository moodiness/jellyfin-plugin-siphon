using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Siphon.Playback;

/// <summary>Separate short-lived request budgets; invalid references never debit authorized work.</summary>
internal sealed class PlaybackRequestBudget(int maximumRequests = 128, int maximumClients = 1024, Func<long>? clock = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private long _window = -1;

    internal bool Allow(string client)
    {
        lock (_gate)
        {
            var now = (clock?.Invoke() ?? Environment.TickCount64) / 1000;
            if (_window != now)
            {
                _window = now;
                _counts.Clear();
            }

            if (_counts.TryGetValue(client, out var count))
            {
                if (count >= maximumRequests) return false;
                _counts[client] = count + 1;
                return true;
            }

            if (_counts.Count >= maximumClients) return false;
            _counts.Add(client, 1);
            return true;
        }
    }

    // Forwarded headers are deliberately not interpreted here; the server's trusted proxy
    // configuration owns RemoteIpAddress. Missing addresses share a bounded anonymous bucket.
    internal static string Client(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

/// <summary>Fail-fast direct-transfer admission; bookkeeping exists only while a transfer is active.</summary>
internal sealed class DirectTransferAdmission(int maximumTransfers = 16, int maximumPerUser = 4)
{
    internal static DirectTransferAdmission Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, int> _users = new();
    private int _active;

    internal IDisposable? TryAcquire(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = _users.GetValueOrDefault(userId);
            if (_active >= maximumTransfers || count >= maximumPerUser) return null;
            _users[userId] = count + 1;
            _active++;
            return new Lease(this, userId);
        }
    }

    private void Release(Guid userId)
    {
        lock (_gate)
        {
            var remaining = _users[userId] - 1;
            if (remaining == 0) _users.Remove(userId);
            else _users[userId] = remaining;
            _active--;
        }
    }

    private sealed class Lease(DirectTransferAdmission owner, Guid userId) : IDisposable
    {
        private DirectTransferAdmission? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(userId);
    }
}
