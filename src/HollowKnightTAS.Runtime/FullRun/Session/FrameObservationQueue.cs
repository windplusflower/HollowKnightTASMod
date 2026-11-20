using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace HollowKnightTAS.Runtime.FullRun
{
    /// <summary>Worker requests execute only when the native boundary services this queue.</summary>
    public sealed class FrameObservationQueue : IDisposable
    {
        private readonly object sync = new object();
        private readonly Queue<Request> pending = new Queue<Request>();
        private readonly Func<bool> wake;
        private bool disposed;
        public FrameObservationQueue(Func<bool> wake) { this.wake = wake; }

        public T Invoke<T>(Func<long, T> read, int timeoutMilliseconds = 10000)
        {
            var request = new Request(frame => read(frame)!);
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(FrameObservationQueue));
                // Failed wake-ups/timeouts must not fill the bounded queue permanently.
                if (pending.Count > 0)
                {
                    var live = pending.ToArray(); pending.Clear();
                    foreach (var item in live) if (!item.Completion.Task.IsCompleted) pending.Enqueue(item);
                }
                if (pending.Count >= 8) throw new InvalidOperationException("Observation queue is busy.");
                pending.Enqueue(request);
                if (!wake()) request.Completion.TrySetException(new InvalidOperationException("Native observation is unavailable."));
            }
            // Task.Wait throws AggregateException for a failed query; preserve the actual error instead.
            var winner = Task.WhenAny(request.Completion.Task, Task.Delay(timeoutMilliseconds)).GetAwaiter().GetResult();
            if (winner != request.Completion.Task)
                request.Completion.TrySetException(new TimeoutException("Observation did not reach a native frame boundary in time."));
            return (T)request.Completion.Task.GetAwaiter().GetResult();
        }

        public void Service(long nativeFrame)
        {
            Request[] requests;
            lock (sync) { requests = pending.ToArray(); pending.Clear(); }
            foreach (var request in requests)
            {
                if (request.Completion.Task.IsCompleted) continue;
                try { request.Completion.TrySetResult(request.Read(nativeFrame)); }
                catch (Exception error) { request.Completion.TrySetException(error); }
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                disposed = true;
                while (pending.Count > 0)
                    pending.Dequeue().Completion.TrySetException(new ObjectDisposedException(nameof(FrameObservationQueue)));
            }
        }

        private sealed class Request
        {
            public Request(Func<long, object> read) { Read = read; }
            public Func<long, object> Read { get; }
            public TaskCompletionSource<object> Completion { get; } = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
