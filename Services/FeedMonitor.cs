using NewsWatch.Models;

namespace NewsWatch.Services;

public class FetchCompletedEventArgs : EventArgs
{
    public required FeedEntry Feed { get; init; }
    public required FetchResult Result { get; init; }

    /// <summary>Identifies the loop that raised this; lets subscribers drop stale results via IsCurrent.</summary>
    public required CancellationToken Token { get; init; }
}

/// <summary>
/// Runs one async fetch loop per feed. Raises FetchCompleted after every fetch
/// (the subscriber marshals to the UI thread and decides what's new).
/// </summary>
public class FeedMonitor : IDisposable
{
    private sealed class Loop
    {
        public readonly CancellationTokenSource Cts = new();
        public readonly SemaphoreSlim Wake = new(0, 1);
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Loop> _loops = new();

    public event EventHandler<FetchCompletedEventArgs>? FetchCompleted;

    public void Start(FeedEntry feed)
    {
        lock (_gate)
        {
            StopLocked(feed.Id);
            var loop = new Loop();
            _loops[feed.Id] = loop;
            _ = RunLoopAsync(feed, loop, loop.Cts.Token);
        }
    }

    // Cancel only: disposing the CTS here would race with the loop, which may still
    // touch it (Task.Delay, WaitAsync) after cancellation.
    public void Stop(Guid feedId)
    {
        lock (_gate)
            StopLocked(feedId);
    }

    private void StopLocked(Guid feedId)
    {
        if (_loops.Remove(feedId, out var old))
            old.Cts.Cancel();
    }

    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var loop in _loops.Values) loop.Cts.Cancel();
            _loops.Clear();
        }
    }

    /// <summary>Fetch now instead of waiting out the interval; the interval restarts afterwards.</summary>
    public void CheckNow(Guid feedId)
    {
        lock (_gate)
        {
            if (_loops.TryGetValue(feedId, out var loop) && loop.Wake.CurrentCount == 0)
                loop.Wake.Release();
        }
    }

    public void CheckAllNow()
    {
        lock (_gate)
        {
            foreach (var loop in _loops.Values)
                if (loop.Wake.CurrentCount == 0) loop.Wake.Release();
        }
    }

    public bool IsCurrent(Guid feedId, CancellationToken token)
    {
        lock (_gate)
            return _loops.TryGetValue(feedId, out var loop) && loop.Cts.Token == token;
    }

    private async Task RunLoopAsync(FeedEntry feed, Loop loop, CancellationToken token)
    {
        try
        {
            await RunLoopCoreAsync(feed, loop, token);
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        catch (Exception)
        {
            // Only reachable if reporting itself failed; never let it go unobserved.
        }
    }

    private async Task RunLoopCoreAsync(FeedEntry feed, Loop loop, CancellationToken token)
    {
        // De-phase the loops so many feeds don't all fetch in one burst at launch.
        await Task.Delay(Random.Shared.Next(300, 4000), token);

        // The URL and interval are captured once: an edit replaces the whole loop.
        var url = feed.Url;
        var interval = TimeSpan.FromSeconds(Math.Max(60, feed.IntervalSeconds));

        while (!token.IsCancellationRequested)
        {
            try
            {
                // FetchAsync parses on the thread pool; this continuation is back on the
                // UI thread (loops start there), so the ETag writes can't race an edit.
                var result = await FeedFetcher.FetchAsync(url, feed.ETag, feed.LastModified, token);
                if (!IsCurrent(feed.Id, token)) return;

                if (result.Outcome == FetchOutcome.Ok)
                {
                    feed.ETag = result.ETag;
                    feed.LastModified = result.LastModified;
                }
                Publish(feed, result, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Report it and keep going: one bad fetch must not stop the feed for the session.
                Publish(feed, new FetchResult { Outcome = FetchOutcome.Error, Error = "APP ERROR" }, token);
            }

            await loop.Wake.WaitAsync(interval, token); // CheckNow releases it early
        }
    }

    private void Publish(FeedEntry feed, FetchResult result, CancellationToken token)
        => FetchCompleted?.Invoke(this, new FetchCompletedEventArgs { Feed = feed, Result = result, Token = token });

    public void Dispose() => StopAll();
}
