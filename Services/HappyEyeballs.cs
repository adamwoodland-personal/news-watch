using System.Net;
using System.Net.Sockets;

namespace NewsWatch.Services;

/// <summary>
/// Connects the way browsers do (Happy Eyeballs, RFC 8305): IPv6 and IPv4
/// addresses are tried interleaved, each with a short head start before the
/// next one joins, and the first to connect wins.
/// .NET's own connect tries addresses one at a time. When one family is routed
/// but silently dropped (phone tethering, flaky ISP IPv6), the first address
/// eats the whole connect timeout and the feed reports TIMEOUT on every check
/// for as long as the network stays that way, while browsers work fine.
/// </summary>
internal static class HappyEyeballs
{
    private const int StaggerMs = 250;
    private const int MaxCandidates = 6;

    public static async Task<Socket> ConnectAsync(IPAddress[] addresses, int port, CancellationToken token)
    {
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

        // Lead with the family the OS ranked first, so a "prefer IPv4" policy is respected.
        var leading = addresses[0].AddressFamily;
        var candidates = Interleave(
                addresses.Where(a => a.AddressFamily == leading),
                addresses.Where(a => a.AddressFamily != leading))
            .Take(MaxCandidates).ToArray();

        // Own token for the race: cancelling the losers must not cancel the winner's HTTP request.
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var attempts = new List<Task<Socket>>();
        var next = 0;
        void StartNext() => attempts.Add(AttemptAsync(candidates[next++], port, raceCts.Token));
        StartNext();

        Exception? lastError = null;
        while (attempts.Count > 0)
        {
            var waitOn = new List<Task>(attempts);
            if (next < candidates.Length && !raceCts.IsCancellationRequested)
                waitOn.Add(Task.Delay(StaggerMs, raceCts.Token));
            var done = await Task.WhenAny(waitOn).ConfigureAwait(false);

            if (done is not Task<Socket> attempt)
            {
                if (!done.IsCanceled) StartNext(); // head start used up, still no winner
                continue;
            }

            attempts.Remove(attempt);
            try
            {
                var socket = await attempt.ConfigureAwait(false);
                raceCts.Cancel();
                // A loser can finish connecting before it sees the cancel; close it rather than leave it to the GC.
                foreach (var loser in attempts)
                    _ = loser.ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully) t.Result.Dispose();
                        else _ = t.Exception;
                    }, TaskScheduler.Default);
                return socket;
            }
            catch (OperationCanceledException)
            {
                // connect timeout reached: keep draining
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (next < candidates.Length && !raceCts.IsCancellationRequested)
                    StartNext(); // failed fast (e.g. no IPv6 route): don't sit out the head start
            }
        }

        // Out of time reports as a timeout; otherwise every address failed outright.
        token.ThrowIfCancellationRequested();
        throw lastError ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private static async Task<Socket> AttemptAsync(IPAddress address, int port, CancellationToken token)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static IEnumerable<T> Interleave<T>(IEnumerable<T> first, IEnumerable<T> second)
    {
        using var a = first.GetEnumerator();
        using var b = second.GetEnumerator();
        bool hasA = a.MoveNext(), hasB = b.MoveNext();
        while (hasA || hasB)
        {
            if (hasA) { yield return a.Current; hasA = a.MoveNext(); }
            if (hasB) { yield return b.Current; hasB = b.MoveNext(); }
        }
    }
}
