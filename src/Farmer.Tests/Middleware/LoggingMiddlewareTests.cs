using Farmer.Core.Middleware;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Farmer.Tests.Middleware;

public class LoggingMiddlewareTests
{
    [Fact]
    public async Task LogsStageNameAndOutcome()
    {
        var logs = new List<string>();
        var logger = new CapturingLogger<LoggingMiddleware>(logs);
        var middleware = new LoggingMiddleware(logger);

        var stage = new FakeStage("TestStage", RunPhase.Loading);
        var state = new RunFlowState { RunId = "run-1" };

        var result = await middleware.InvokeAsync(stage, state,
            () => Task.FromResult(StageResult.Succeeded("TestStage")));

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.Contains(logs, l => l.Contains("TestStage") && l.Contains("executing"));
        Assert.Contains(logs, l => l.Contains("TestStage") && l.Contains("Success"));
    }

    [Fact]
    public async Task LogsFailureError()
    {
        var logs = new List<string>();
        var logger = new CapturingLogger<LoggingMiddleware>(logs);
        var middleware = new LoggingMiddleware(logger);

        var stage = new FakeStage("BadStage", RunPhase.Delivering);
        var state = new RunFlowState { RunId = "run-1" };

        var result = await middleware.InvokeAsync(stage, state,
            () => Task.FromResult(StageResult.Failed("BadStage", "kaboom")));

        Assert.Equal(StageOutcome.Failure, result.Outcome);
        Assert.Contains(logs, l => l.Contains("kaboom"));
    }

    [Fact]
    public async Task CallsNextDelegate()
    {
        var logs = new List<string>();
        var logger = new CapturingLogger<LoggingMiddleware>(logs);
        var middleware = new LoggingMiddleware(logger);

        var nextCalled = false;
        var stage = new FakeStage("S", RunPhase.Loading);
        var state = new RunFlowState();

        await middleware.InvokeAsync(stage, state, () =>
        {
            nextCalled = true;
            return Task.FromResult(StageResult.Succeeded("S"));
        });

        Assert.True(nextCalled);
    }

    // --- Helpers ---

    private sealed class FakeStage : IWorkflowStage
    {
        public string Name { get; }
        public RunPhase Phase { get; }
        public FakeStage(string name, RunPhase phase) { Name = name; Phase = phase; }
        public Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
            => Task.FromResult(StageResult.Succeeded(Name));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _logs;
        public CapturingLogger(List<string> logs) { _logs = logs; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _logs.Add(formatter(state, exception));
        }
    }
}
