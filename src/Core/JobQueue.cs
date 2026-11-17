using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;

namespace Heimskringla.Core;

/// <summary>
/// Simple priority queue with its own worker threads. Work runs in the background; the result is delivered
/// back on the main thread via ProcessCompleted() with a per-frame time budget.
/// Lowest Priority runs first (typically distance to the player).
/// </summary>
public sealed class JobQueue : IDisposable
{
    public sealed class Job
    {
        public float Priority;
        public volatile bool Cancelled;
        internal Func<object> Work;
        internal Action<object> Done;
    }

    private readonly List<Job> _pending = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ConcurrentQueue<(Job job, object result)> _completed = new();
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly Thread[] _threads;
    private volatile bool _stop;
    private int _running;

    public int PendingCount { get { lock (_lock) return _pending.Count; } }
    public int RunningCount => _running;
    public int CompletedWaiting => _completed.Count;

    public JobQueue(int threads)
    {
        _threads = new Thread[Math.Max(1, threads)];
        for (int i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"Heimskringla-worker-{i}", Priority = ThreadPriority.BelowNormal };
            _threads[i].Start();
        }
    }

    public Job Enqueue(float priority, Func<object> work, Action<object> done)
    {
        var job = new Job { Priority = priority, Work = work, Done = done };
        lock (_lock) _pending.Add(job);
        _signal.Release();
        return job;
    }

    private void WorkerLoop()
    {
        while (!_stop)
        {
            _signal.Wait();
            if (_stop) return;
            Job job = null;
            lock (_lock)
            {
                int best = -1;
                float bp = float.MaxValue;
                for (int i = 0; i < _pending.Count; i++)
                {
                    if (_pending[i].Priority < bp) { bp = _pending[i].Priority; best = i; }
                }
                if (best >= 0)
                {
                    job = _pending[best];
                    _pending[best] = _pending[^1];
                    _pending.RemoveAt(_pending.Count - 1);
                }
            }
            if (job == null || job.Cancelled) continue;
            Interlocked.Increment(ref _running);
            try
            {
                var result = job.Work();
                if (!job.Cancelled) _completed.Enqueue((job, result));
            }
            catch (Exception e)
            {
                _errors.Enqueue(e);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    /// <summary>Called from the main thread. Delivers finished results within the time budget.</summary>
    public void ProcessCompleted(double budgetMs)
    {
        while (_errors.TryDequeue(out var e)) GD.PushError($"Job failed: {e}");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < budgetMs && _completed.TryDequeue(out var item))
        {
            if (item.job.Cancelled) continue;
            item.job.Done(item.result);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _signal.Release(_threads.Length);
    }
}
