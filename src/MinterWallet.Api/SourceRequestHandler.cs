using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace MinterWallet.Api;

// One budget for every HTTP client talking to the same origin: discovery, balances, notifications and telemetry.
// This stays below the public gate's 10 requests/s and 10 simultaneous requests. Only reads may be retried.
// Foreground actions (the selected address, user clicks, signing previews) step ahead of background scans and watches.
public sealed class SourceRequestHandler(HttpMessageHandler inner, TimeSpan minimumInterval, long maximumBodyBytes = 32 * 1024 * 1024) : DelegatingHandler(inner)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(160);
    public static readonly HttpRequestOptionsKey<bool> BackgroundKey = new("MinterWallet.Background");
    private static readonly AsyncLocal<bool> background = new();
    private static readonly ConcurrentDictionary<string, Budget> Budgets = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsBackground => background.Value;

    public static BackgroundScope EnterBackground()
    {
        var previous = background.Value;
        background.Value = true;
        return new BackgroundScope(previous);
    }

    public readonly struct BackgroundScope(bool previous) : IDisposable
    {
        public void Dispose() => background.Value = previous;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var canRetry = request.Method == HttpMethod.Get;
        var low = background.Value || (request.Options.TryGetValue(BackgroundKey, out var b) && b);
        var budget = Budgets.GetOrAdd($"{uri.Scheme}://{uri.IdnHost}:{uri.Port}", _ => new Budget(minimumInterval));
        for (var attempt = 0; ; attempt++)
        {
            using var retry = attempt == 0 ? null : CloneRead(request, low);
            await budget.Slots.WaitAsync(low, ct);
            try
            {
                await budget.WaitTurnAsync(low, ct);
                var response = await base.SendAsync(retry ?? request, ct);
                // Keep the slot until the body is read, not merely until headers arrive.
                try { await response.Content.LoadIntoBufferAsync(maximumBodyBytes, ct); }
                catch { response.Dispose(); throw; }
                if (response.StatusCode != HttpStatusCode.TooManyRequests) return response;
                var after = response.Headers.RetryAfter;
                var delay = after?.Delta ?? (after?.Date is { } at ? at - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1 << Math.Min(attempt, 2)));
                budget.Pause(TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 50, 120_000)));
                if (!canRetry || attempt >= 2) return response;
                response.Dispose();
            }
            finally { budget.Slots.Release(); }
        }
    }

    private static HttpRequestMessage CloneRead(HttpRequestMessage request, bool low)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (low) copy.Options.Set(BackgroundKey, true);
        return copy;
    }

    private sealed class PriorityGate(int maxHigh, int maxLow)
    {
        private readonly object sync = new();
        private readonly Queue<TaskCompletionSource<bool>> high = new();
        private readonly Queue<TaskCompletionSource<bool>> low = new();
        private int active;

        public Task WaitAsync(bool isLow, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
            TaskCompletionSource<bool> waiter;
            lock (sync)
            {
                var limit = isLow ? maxLow : maxHigh;
                if (active < limit && (!isLow || high.Count == 0))
                {
                    active++;
                    return Task.CompletedTask;
                }
                waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                (isLow ? low : high).Enqueue(waiter);
            }
            return ct.CanBeCanceled ? WaitWithCancelAsync(waiter, ct) : waiter.Task;
        }

        private static async Task WaitWithCancelAsync(TaskCompletionSource<bool> waiter, CancellationToken ct)
        {
            await using var registration = ct.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), waiter);
            await waiter.Task;
        }

        public void Release()
        {
            lock (sync)
            {
                var remaining = active - 1;
                if (remaining < maxHigh)
                    while (high.TryDequeue(out var next))
                        if (next.TrySetResult(true)) return;
                if (remaining < maxLow)
                    while (low.TryDequeue(out var next))
                        if (next.TrySetResult(true)) return;
                active = remaining;
            }
        }
    }

    private sealed class Budget(TimeSpan interval)
    {
        public PriorityGate Slots { get; } = new(4, 3);
        private readonly PriorityGate scheduler = new(1, 1);
        private readonly object clock = new();
        private long next, pausedUntil;
        public async Task WaitTurnAsync(bool low, CancellationToken ct)
        {
            await scheduler.WaitAsync(low, ct);
            try
            {
                while (true)
                {
                    TimeSpan wait;
                    lock (clock)
                    {
                        var now = Stopwatch.GetTimestamp();
                        var ticks = Math.Max(next, pausedUntil) - now;
                        if (ticks <= 0) { next = now + (long)(interval.TotalSeconds * Stopwatch.Frequency); return; }
                        wait = TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);
                    }
                    await Task.Delay(wait, ct);
                }
            }
            finally { scheduler.Release(); }
        }
        public void Pause(TimeSpan duration)
        {
            lock (clock) pausedUntil = Math.Max(pausedUntil, Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency));
        }
    }
}
