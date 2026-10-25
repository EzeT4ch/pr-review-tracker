using System.Collections.Concurrent;
using Prt.Core.Domain;
using Prt.Core.Panel;

namespace Prt.Web;

public enum JobStatus
{
    Running,
    Succeeded,
    Failed,
}

/// <summary>A background task started from the panel (analysis or sync of a PR).</summary>
public sealed class Job : IPanelUi
{
    private readonly List<string> _log = [];

    public required int Id { get; init; }
    public required string Kind { get; init; }
    public required PrRef Pr { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? EndedAt { get; internal set; }
    public JobStatus Status { get; internal set; } = JobStatus.Running;

    public IReadOnlyList<string> Log
    {
        get { lock (_log) return _log.ToList(); }
    }

    public void Info(string message) => Add(message);
    public void Warn(string message) => Add("⚠ " + message);

    public void ReviewerStarted(ReviewerDefinition reviewer, string agent, bool resumed) =>
        Add($"{reviewer.Title}: {agent}, {(resumed ? "sesión retomada" : "sesión nueva")}…");

    public void ReviewerFinished(ReviewerOutcome outcome) =>
        Add(outcome.Report is { } r
            ? $"{outcome.Reviewer.Title}: {ReviewStandard.Label(r.Verdict)} ({r.OpenFindings.Count()} hallazgos abiertos)"
            : $"{outcome.Reviewer.Title}: {(outcome.Result.Status == ResultStatus.Invalid ? "respuesta fuera de formato" : "falló")} — {outcome.Result.Error}");

    internal void Add(string line)
    {
        lock (_log)
            _log.Add($"{DateTimeOffset.Now:HH:mm:ss} {line}");
    }
}

/// <summary>Runs jobs one per PR at a time and keeps the recent ones for display.</summary>
public sealed class JobRunner
{
    private const int Keep = 50;
    private readonly ConcurrentDictionary<int, Job> _jobs = new();
    private int _nextId;

    public bool Busy => _jobs.Values.Any(j => j.Status == JobStatus.Running);

    public IReadOnlyList<Job> All => _jobs.Values.OrderByDescending(j => j.Id).ToList();

    public Job? Get(int id) => _jobs.GetValueOrDefault(id);

    public Job? Running(PrRef pr) => _jobs.Values.FirstOrDefault(j => j.Pr == pr && j.Status == JobStatus.Running);

    public Job? Latest(PrRef pr) => _jobs.Values.Where(j => j.Pr == pr).MaxBy(j => j.Id);

    /// <summary>Starts a job, or returns the one already running for that PR.</summary>
    public Job Start(string kind, PrRef pr, Func<Job, Task> work)
    {
        lock (_jobs)
        {
            if (Running(pr) is { } running)
                return running;

            var job = new Job { Id = Interlocked.Increment(ref _nextId), Kind = kind, Pr = pr };
            _jobs[job.Id] = job;
            foreach (var old in _jobs.Values.Where(j => j.Status != JobStatus.Running).OrderByDescending(j => j.Id).Skip(Keep))
                _jobs.TryRemove(old.Id, out _);

            _ = Task.Run(async () =>
            {
                try
                {
                    await work(job);
                    job.Status = JobStatus.Succeeded;
                    job.Add("Listo.");
                }
                catch (Exception ex)
                {
                    job.Status = JobStatus.Failed;
                    job.Add("Error: " + ex.Message);
                }
                finally
                {
                    job.EndedAt = DateTimeOffset.Now;
                }
            });
            return job;
        }
    }
}
