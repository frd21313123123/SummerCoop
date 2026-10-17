using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace SummerCoop.Core
{
    // Managed IO only. Unity objects and gameplay commands must stay on the caller thread.
    public sealed class BackgroundIo
    {
        sealed class Work { public Action Run; public long Enqueued; }
        readonly object gate = new object();
        readonly Queue<Work> queue = new Queue<Work>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread thread;
        readonly int capacity;
        bool closed, active;
        long activeSince;
        Exception error;
        public BackgroundIo(string name, int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException("capacity");
            this.capacity = capacity;
            thread = new Thread(Loop); thread.Name = name; thread.IsBackground = true; thread.Start();
        }
        public bool TryPost(Action work)
        {
            if (work == null) throw new ArgumentNullException("work");
            lock (gate)
            {
                if (closed || queue.Count >= capacity) return false;
                queue.Enqueue(new Work { Run = work, Enqueued = Stopwatch.GetTimestamp() }); wake.Set(); return true;
            }
        }
        public int Pending { get { lock (gate) return queue.Count + (active ? 1 : 0); } }
        public double OldestMilliseconds { get { lock (gate) { long since = active ? activeSince : queue.Count > 0 ? queue.Peek().Enqueued : 0; return since == 0 ? 0 : (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency; } } }
        public Exception TakeError() { lock (gate) { Exception result = error; error = null; return result; } }
        public bool Complete(int timeoutMilliseconds)
        {
            lock (gate) { closed = true; wake.Set(); }
            return thread.Join(timeoutMilliseconds);
        }
        void Loop()
        {
            while (true)
            {
                Work work = null;
                lock (gate)
                {
                    if (queue.Count > 0) { work = queue.Dequeue(); active = true; activeSince = work.Enqueued; }
                    else { active = false; if (closed) return; }
                }
                if (work == null) { wake.WaitOne(); continue; }
                try { work.Run(); }
                catch (Exception e) { lock (gate) error = e; }
                finally { lock (gate) active = false; }
            }
        }
    }
}
