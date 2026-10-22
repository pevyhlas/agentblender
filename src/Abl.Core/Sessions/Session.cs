using Abl.Core.Workers;

namespace Abl.Core.Sessions;

public enum SessionState
{
    Ready,
    Busy,
    Dead,
}

/// <summary>One agent design session: a warm worker + a checkpoint head + a journal.</summary>
public sealed class Session(string id, string project, WorkerHandle worker, string head)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Id { get; } = id;
    public string Project { get; } = project;
    public WorkerHandle Worker { get; internal set; } = worker;
    public string Head { get; internal set; } = head;
    public int Seq { get; internal set; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public volatile SessionState State = SessionState.Ready;

    /// <summary>FR-003: one op in flight per session — concurrent calls are rejected.</summary>
    public IDisposable EnterOp(Action? workerLost = null)
    {
        if (State == SessionState.Dead)
        {
            throw new AblException(
                "worker_died",
                $"session {Id}'s worker is dead",
                hint: $"branch a new session from the last checkpoint: checkpoint_branch {Head[..12]}",
                lastCheckpoint: Head);
        }

        if (!_gate.Wait(0))
        {
            throw new AblException(
                "session_busy",
                $"session {Id} already has an op in flight; ops on a session are serialized",
                hint: "wait for the current op to finish, or open another session");
        }

        State = SessionState.Busy;
        return new OpScope(this, workerLost);
    }

    private sealed class OpScope(Session session, Action? workerLost) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (session.State != SessionState.Dead && !session.Worker.IsAlive)
                {
                    session.State = SessionState.Dead;
                    workerLost?.Invoke();
                }
                else if (session.State != SessionState.Dead)
                {
                    session.State = SessionState.Ready;
                }
            }
            finally { session._gate.Release(); }
        }
    }
}

public sealed record SessionInfo(
    string Id,
    string Project,
    string State,
    string Head,
    string WorkerId,
    DateTimeOffset CreatedAt);
