using HappAccessible.Models;

namespace HappAccessible.Services;

public sealed class SessionHealthMonitor
{
    private int _healthFailStreak;
    private int _healthTickCount;
    private DateTime _lastRefreshAttemptUtc = DateTime.MinValue;

    public int HealthFailStreak => _healthFailStreak;

    public void ResetFailStreak() => _healthFailStreak = 0;

    public async Task<HealthTickResult> RunTickAsync(HealthTickContext ctx, bool forceImmediate = false)
    {
        if (ctx.IsBusy || !ctx.IsConnected)
            return HealthTickResult.None;

        if (ctx.IsAmnezia)
            return await RunAwgTickAsync(ctx).ConfigureAwait(false);

        if (ctx.ConnectedServer is null)
            return HealthTickResult.None;

        _healthTickCount++;
        if (_healthTickCount % 20 == 0 && ctx.SystemProxyEnabled)
            ctx.RefreshProxy();

        var refreshMinutes = Math.Clamp(ctx.SessionRefreshMinutes, 0, 720);
        if (refreshMinutes > 0
            && ctx.SessionConnectedUtc != default
            && DateTime.UtcNow - ctx.SessionConnectedUtc >= TimeSpan.FromMinutes(refreshMinutes))
        {
            if (DateTime.UtcNow - _lastRefreshAttemptUtc < TimeSpan.FromMinutes(30))
                return HealthTickResult.None;

            _lastRefreshAttemptUtc = DateTime.UtcNow;
            return HealthTickResult.RefreshSession;
        }

        if (!ctx.CoreRunning)
            return HealthTickResult.Failure("ядро прокси завершилось");

        var selectiveRouting = ctx.RoutingModeTag is "proxy-list" or "app-proxy" or "app-bypass";
        if (selectiveRouting && !forceImmediate)
        {
            if (!await ConnectivityProbe.ProbeMixedPortAsync(ctx.MixedPort).ConfigureAwait(false))
                return HealthTickResult.Failure("локальный mixed-порт не отвечает");
            _healthFailStreak = 0;
            return HealthTickResult.None;
        }

        var (ok, detail) = await ctx.ProbeFullSessionAsync().ConfigureAwait(false);
        if (ok)
        {
            _healthFailStreak = 0;
            return HealthTickResult.None;
        }

        _healthFailStreak++;
        if (_healthFailStreak < 2)
            return HealthTickResult.Retry(detail);

        _healthFailStreak = 0;
        return HealthTickResult.Failure($"туннель не отвечает ({detail})");
    }

    private Task<HealthTickResult> RunAwgTickAsync(HealthTickContext ctx)
    {
        if (ctx.AwgTunnelRunning)
        {
            _healthFailStreak = 0;
            return Task.FromResult(HealthTickResult.None);
        }

        _healthFailStreak++;
        if (_healthFailStreak < 2)
            return Task.FromResult(HealthTickResult.Retry("AmneziaWG туннель не отвечает"));

        _healthFailStreak = 0;
        return Task.FromResult(HealthTickResult.Failure("AmneziaWG туннель остановился"));
    }
}

public sealed class HealthTickContext
{
    public required bool IsBusy { get; init; }
    public required bool IsConnected { get; init; }
    public required bool IsAmnezia { get; init; }
    public required bool AwgTunnelRunning { get; init; }
    public required bool CoreRunning { get; init; }
    public required bool SystemProxyEnabled { get; init; }
    public required int MixedPort { get; init; }
    public required int SessionRefreshMinutes { get; init; }
    public required DateTime SessionConnectedUtc { get; init; }
    public required string RoutingModeTag { get; init; }
    public ServerProfile? ConnectedServer { get; init; }
    public required Action RefreshProxy { get; init; }
    public required Func<Task<(bool Ok, string Detail)>> ProbeFullSessionAsync { get; init; }
}

public readonly struct HealthTickResult
{
    public enum Kind { None, Retry, Failure, RefreshSession }

    public Kind ResultKind { get; init; }
    public string? Detail { get; init; }

    public static HealthTickResult None => new() { ResultKind = Kind.None };
    public static HealthTickResult RefreshSession => new() { ResultKind = Kind.RefreshSession };
    public static HealthTickResult Retry(string detail) =>
        new() { ResultKind = Kind.Retry, Detail = detail };
    public static HealthTickResult Failure(string detail) =>
        new() { ResultKind = Kind.Failure, Detail = detail };
}
