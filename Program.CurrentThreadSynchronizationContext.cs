using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading;

namespace CSharpScriptRunner
{
    static partial class Program
    {
        sealed class CurrentThreadSynchronizationContext : System.Threading.SynchronizationContext
        {
            readonly BlockingCollection<(SendOrPostCallback Callback, object State)> _queue = new();
            // readonly BlockingCollection<(SendOrPostCallback Callback, object State, ManualResetEventSlim Signal)> _queue = new();

            CurrentThreadSynchronizationContext() { }

            public override void Send(SendOrPostCallback d, object state)
            {
                // var signal = new ManualResetEventSlim(false);
                // _queue.Add((d, state, signal));
                // signal.Wait();
                throw new InvalidOperationException();
            }

            public override void Post(SendOrPostCallback d, object state)
            {
                _queue.Add((d, state));
                // _queue.Add((d, state, null));
            }

            public static int Run(Func<Task<ErrorCodes>> main)
            {
                var syncCtx = new CurrentThreadSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(syncCtx);
                var mainTask = main();
                mainTask.ContinueWith(t => syncCtx._queue.CompleteAdding());
                foreach (var item in syncCtx._queue.GetConsumingEnumerable())
                {
                    item.Callback(item.State);
                    // item.Signal?.Set();
                }
                return (int)mainTask.Result;
            }
        }
    }
}
