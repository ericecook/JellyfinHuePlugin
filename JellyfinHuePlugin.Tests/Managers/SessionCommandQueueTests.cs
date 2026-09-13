using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JellyfinHuePlugin.Managers;
using Xunit;

namespace JellyfinHuePlugin.Tests.Managers
{
    /// <summary>
    /// Ordering and latest-wins semantics. Commands are lambdas over TaskCompletionSource
    /// gates so every test controls exactly when a command finishes.
    /// </summary>
    public class SessionCommandQueueTests
    {
        private readonly SessionCommandQueue _queue = new();
        private readonly List<string> _log = new();

        private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Fact]
        public async Task RunsCommandsInOrder()
        {
            var firstGate = Gate();

            var first = _queue.Enqueue(async _ => { _log.Add("first:start"); await firstGate.Task; _log.Add("first:end"); });
            var second = _queue.Enqueue(_ => { _log.Add("second"); return Task.CompletedTask; });

            await Task.Delay(50);
            _log.Should().Equal("first:start");

            firstGate.SetResult();
            await Task.WhenAll(first, second);

            _log.Should().Equal("first:start", "first:end", "second");
        }

        [Fact]
        public async Task Enqueue_CancelsRunningCommandsToken()
        {
            var started = Gate();
            CancellationToken observed = default;

            var first = _queue.Enqueue(async ct =>
            {
                observed = ct;
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });
            await started.Task;

            var second = _queue.Enqueue(_ => Task.CompletedTask);
            await Task.WhenAll(first, second);

            observed.IsCancellationRequested.Should().BeTrue();
        }

        [Fact]
        public async Task NewCommandStartsOnlyAfterCancelledOneExits()
        {
            var started = Gate();
            var release = Gate();

            var first = _queue.Enqueue(async ct =>
            {
                started.SetResult();
                await release.Task; // ignores the token on purpose: simulates a call that cannot be interrupted
                _log.Add("first:end");
            });
            await started.Task;

            var second = _queue.Enqueue(_ => { _log.Add("second"); return Task.CompletedTask; });
            await Task.Delay(50);
            _log.Should().BeEmpty();

            release.SetResult();
            await Task.WhenAll(first, second);

            _log.Should().Equal("first:end", "second");
        }

        [Fact]
        public async Task SupersededBeforeStart_NeverRuns()
        {
            var started = Gate();
            var release = Gate();

            var first = _queue.Enqueue(async _ => { started.SetResult(); await release.Task; _log.Add("first"); });
            await started.Task;
            var second = _queue.Enqueue(_ => { _log.Add("second"); return Task.CompletedTask; });
            var third = _queue.Enqueue(_ => { _log.Add("third"); return Task.CompletedTask; });

            release.SetResult();
            await Task.WhenAll(first, second, third);

            _log.Should().Equal("first", "third");
        }

        [Fact]
        public async Task ThrowingCommand_DoesNotBlockNext()
        {
            var first = _queue.Enqueue(_ => throw new InvalidOperationException("boom"));
            var second = _queue.Enqueue(_ => { _log.Add("second"); return Task.CompletedTask; });

            await Task.WhenAll(first, second);

            first.IsCompletedSuccessfully.Should().BeTrue();
            _log.Should().Equal("second");
        }

        [Fact]
        public async Task Cancel_CancelsRunningCommand()
        {
            var started = Gate();
            var first = _queue.Enqueue(async ct =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });
            await started.Task;

            _queue.Cancel();
            await first;

            first.IsCompletedSuccessfully.Should().BeTrue();
        }

        [Fact]
        public async Task ReturnedTask_CompletesAfterCommand()
        {
            var release = Gate();

            var task = _queue.Enqueue(async _ => { await release.Task; _log.Add("done"); });
            await Task.Delay(50);
            task.IsCompleted.Should().BeFalse();

            release.SetResult();
            await task;

            _log.Should().Equal("done");
        }
    }
}
