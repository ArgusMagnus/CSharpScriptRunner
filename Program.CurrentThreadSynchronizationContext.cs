using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading;

namespace CSharpScriptRunner
{
    static partial class Program
    {
        sealed class SynchronizationContextScope : IDisposable
        {
            readonly SynchronizationContext _syncCtx;
            bool _isDisposed = true;
            public SynchronizationContextScope() => _syncCtx = SynchronizationContext.Current;

            public void Install(SynchronizationContext synchronizationContext)
            {
                Dispose();
                SynchronizationContext.SetSynchronizationContext(synchronizationContext);
                _isDisposed = false;
            }

            public System.Runtime.CompilerServices.YieldAwaitable InstallAndYield(SynchronizationContext synchronizationContext)
            {
                Install(synchronizationContext);
                return Task.Yield();
            }

            public void Dispose()
            {
                if (_isDisposed)
                    return;
                _isDisposed = true;
                var current = SynchronizationContext.Current;
                if (current == _syncCtx)
                    return;
                if (current is IDisposable disposable)
                    disposable.Dispose();
                SynchronizationContext.SetSynchronizationContext(_syncCtx);
            }
        }

        sealed class CurrentThreadSynchronizationContext : System.Threading.SynchronizationContext, IDisposable
        {
            readonly BlockingCollection<(SendOrPostCallback Callback, object State)> _queue = new();
            int _isRunning;
            public CurrentThreadSynchronizationContext() { }
            public override void Send(SendOrPostCallback d, object state) => throw new InvalidOperationException();
            public override void Post(SendOrPostCallback d, object state)
            {
                _queue.Add((d, state));
                if (Interlocked.Exchange(ref _isRunning, 1) != 0)
                    return;
                foreach (var item in _queue.GetConsumingEnumerable())
                    item.Callback(item.State);
            }

            public void Dispose() => _queue.CompleteAdding();
        }
    }
}
