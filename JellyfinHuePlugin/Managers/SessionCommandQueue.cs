using System;
using System.Threading;
using System.Threading.Tasks;

namespace JellyfinHuePlugin.Managers
{
    /// <summary>
    /// Latest-wins command queue for one session. Enqueue cancels the running command's token
    /// and chains the new command behind it, so a superseded command exits at its next await
    /// before the new one sends anything. Commands never overlap.
    /// </summary>
    internal sealed class SessionCommandQueue
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _current;
        private Task _tail = Task.CompletedTask;

        /// <summary>
        /// Returns a task that completes when the command has finished, been superseded before
        /// it started, or thrown. The task never faults: cancellation and exceptions are
        /// absorbed here; the caller logs inside the command.
        /// </summary>
        public Task Enqueue(Func<CancellationToken, Task> command)
        {
            CancellationTokenSource? previous;
            Task tail;
            lock (_gate)
            {
                previous = _current;
                var source = new CancellationTokenSource();
                _current = source;
                // ContinueWith runs the command off this thread, so nothing executes under the gate.
                _tail = _tail
                    .ContinueWith(_ => RunAsync(command, source), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                    .Unwrap();
                tail = _tail;
            }

            // Cancel outside the gate: Cancel() can run continuations synchronously, and those
            // continuations (e.g. the previous RunAsync's cleanup) may need the gate themselves.
            // The previous command may have finished and disposed its source between the
            // capture above and this call; a disposed source means there is nothing to cancel.
            try
            {
                previous?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            return tail;
        }

        /// <summary>Cancels the running command and any command not yet started.</summary>
        public void Cancel()
        {
            lock (_gate)
            {
                _current?.Cancel();
            }
        }

        private async Task RunAsync(Func<CancellationToken, Task> command, CancellationTokenSource source)
        {
            try
            {
                if (!source.IsCancellationRequested)
                {
                    await command(source.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Absorbed by contract: the command logs its own failures and cancellations.
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_current, source))
                    {
                        _current = null;
                    }
                }

                source.Dispose();
            }
        }
    }
}
