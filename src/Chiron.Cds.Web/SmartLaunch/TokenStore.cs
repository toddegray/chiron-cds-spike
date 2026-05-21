using System.Collections.Concurrent;

namespace Chiron.Cds.Web.SmartLaunch;

/// <summary>
/// Storage abstraction for in-flight launches and completed SMART sessions.
/// </summary>
/// <remarks>
/// Production would back this with Data Protection API-encrypted Redis (or
/// distributed cache) so sessions survive restarts and scale-out and so
/// access tokens are not cleartext at rest. The in-memory implementation
/// keeps the spike runnable on a laptop with zero infrastructure.
/// </remarks>
public interface ITokenStore
{
    void SavePending(PendingLaunch launch);
    PendingLaunch? TakePending(string state);

    void SaveSession(SmartSession session);
    SmartSession? GetSession(string sessionId);
    void RemoveSession(string sessionId);
}

/// <summary>In-memory <see cref="ITokenStore"/>. Suitable for the spike, not for production.</summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private readonly ConcurrentDictionary<string, PendingLaunch> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SmartSession> _sessions = new(StringComparer.Ordinal);
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(10);

    public void SavePending(PendingLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        CleanupPending();
        _pending[launch.State] = launch;
    }

    public PendingLaunch? TakePending(string state)
    {
        if (string.IsNullOrEmpty(state)) return null;
        return _pending.TryRemove(state, out var p) && !p.IsExpired(PendingTtl) ? p : null;
    }

    public void SaveSession(SmartSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sessions[session.SessionId] = session;
    }

    public SmartSession? GetSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        if (!_sessions.TryGetValue(sessionId, out var s)) return null;
        if (s.IsExpired) { _sessions.TryRemove(sessionId, out _); return null; }
        return s;
    }

    public void RemoveSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        _sessions.TryRemove(sessionId, out _);
    }

    private void CleanupPending()
    {
        foreach (var kv in _pending)
        {
            if (kv.Value.IsExpired(PendingTtl))
                _pending.TryRemove(kv.Key, out _);
        }
    }
}
