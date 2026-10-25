using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace PhotoFlow.Infrastructure.Wpd;

/// <summary>All device calls and releases run on one MTA thread; COM never reaches the UI thread.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ComWorker : IAsyncDisposable
{
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread thread;
    public ComWorker()
    {
        thread = new Thread(() => { foreach (var action in queue.GetConsumingEnumerable()) action(); })
            { IsBackground = true, Name = "PhotoFlow WPD" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken ct = default)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            queue.Add(() =>
            {
                if (ct.IsCancellationRequested) { result.TrySetCanceled(ct); return; }
                try { result.TrySetResult(action()); }
                catch (OperationCanceledException e) { result.TrySetCanceled(e.CancellationToken); }
                catch (Exception e) { result.TrySetException(e); }
            }, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or OperationCanceledException)
        { result.TrySetException(e); }
        return result.Task;
    }

    public async ValueTask DisposeAsync()
    {
        queue.CompleteAdding();
        await Task.Run(thread.Join);
        queue.Dispose();
    }
}
