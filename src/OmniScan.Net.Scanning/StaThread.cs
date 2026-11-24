using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace OmniScan.Net.Scanning;

/// <summary>
/// One background STA thread that runs queued work in order. WIA objects are apartment-threaded,
/// so every call to them goes through the same thread. The thread starts on first use.
/// </summary>
internal sealed class StaThread : IDisposable
{
    private readonly string _name;
    private readonly BlockingCollection<Action> _queue = new();
    private Thread? _thread;

    public StaThread(string name)
    {
        _name = name;
    }

    public Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EnsureStarted();
        _queue.Add(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception e)
            {
                completion.TrySetException(e);
            }
        });
        return completion.Task;
    }

    public void Dispose() => _queue.CompleteAdding();

    private void EnsureStarted()
    {
        if (_thread is not null)
            return;
        lock (_queue)
        {
            if (_thread is not null)
                return;
            Thread thread = new(Loop) { IsBackground = true, Name = _name };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            _thread = thread;
        }
    }

    private void Loop()
    {
        foreach (Action action in _queue.GetConsumingEnumerable())
            action();
    }
}
