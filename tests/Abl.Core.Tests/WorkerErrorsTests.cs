using System;
using Abl.Core;
using Abl.Core.Workers;
using Xunit;

namespace Abl.Core.Tests;

/// <summary>
/// The surface backstop that keeps a transient-worker failure (render/replay/batch) from
/// escaping as a non-structured 500. Session ops convert their own worker errors upstream.
/// </summary>
public sealed class WorkerErrorsTests
{
    [Fact]
    public void WorkerBadRequestStaysAClientError()
    {
        Assert.True(WorkerErrors.TryMap(
            new WorkerOpException("bad_request", "unknown engine 'x'", "tb"), out var mapped));
        Assert.Equal("bad_request", mapped.Kind);
        Assert.Equal("unknown engine 'x'", mapped.Message);
        Assert.Equal("tb", mapped.WorkerTraceback);
    }

    [Fact]
    public void OtherWorkerOpFailuresAreExecFailed()
    {
        Assert.True(WorkerErrors.TryMap(
            new WorkerOpException("KeyError", "'boom'", "tb"), out var mapped));
        Assert.Equal("exec_failed", mapped.Kind);
        Assert.Equal("tb", mapped.WorkerTraceback);
    }

    [Fact]
    public void TimeoutAndDeathMapToTheirKindsWithATransientRetryHint()
    {
        Assert.True(WorkerErrors.TryMap(
            new WorkerTimeoutException("render", TimeSpan.FromSeconds(30)), out var timeout));
        Assert.Equal("timeout", timeout.Kind);
        Assert.Contains("transient", timeout.Hint);

        Assert.True(WorkerErrors.TryMap(new WorkerDeadException("socket broke"), out var dead));
        Assert.Equal("worker_died", dead.Kind);
        Assert.Contains("transient", dead.Hint);
    }

    [Fact]
    public void OpKindKeepsBadRequestAndCollapsesEverythingElse()
    {
        Assert.Equal("bad_request", WorkerErrors.OpKind(WorkerErrors.WorkerBadRequest));
        Assert.Equal("exec_failed", WorkerErrors.OpKind("KeyError"));
        Assert.Equal("exec_failed", WorkerErrors.OpKind("RuntimeError"));
    }

    [Fact]
    public void UnrelatedExceptionsAreNotMapped()
    {
        Assert.False(WorkerErrors.TryMap(new InvalidOperationException("nope"), out _));
        Assert.False(WorkerErrors.TryMap(
            new AblException("bad_request", "already structured"), out _));
    }
}
