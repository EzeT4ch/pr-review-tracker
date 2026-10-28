using System.Net;
using Prt.Core.Agents;
using Prt.Core.Bitbucket;
using Prt.Core.Domain;
using Prt.Core.Panel;
using Prt.Core.Review;

namespace Prt.Core.Tests;

internal sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (this)
            return _now = _now.AddMinutes(1);
    }
}

internal sealed class FakeRunner : IProcessRunner
{
    public List<(string Command, List<string> Args, string Cwd)> Interactive { get; } = [];
    public List<(string Command, List<string> Args, string Cwd)> Captured { get; } = [];
    public Action<List<string>>? OnInteractive { get; set; }
    public Func<List<string>, ProcessResult> OnCaptured { get; set; } = _ => new ProcessResult(0, "", "");

    public Task<int> RunInteractiveAsync(string command, IReadOnlyList<string> args, string cwd, CancellationToken ct = default)
    {
        Interactive.Add((command, args.ToList(), cwd));
        OnInteractive?.Invoke(args.ToList());
        return Task.FromResult(0);
    }

    public Task<ProcessResult> RunCapturedAsync(string command, IReadOnlyList<string> args, string cwd, CancellationToken ct = default)
    {
        lock (Captured)
            Captured.Add((command, args.ToList(), cwd));
        return Task.FromResult(OnCaptured(args.ToList()));
    }
}

internal sealed class FakeBitbucket : IPullRequestSource
{
    public PullRequestInfo? Info { get; set; }
    public List<PrCommit> Commits { get; set; } = [];
    public bool Offline { get; set; }
    public string? Diff { get; set; } = "diff --git a/src/Pay.cs b/src/Pay.cs\n+ cobrar();\n";

    public Task<PullRequestInfo> GetPullRequestAsync(PrRef prRef, CancellationToken ct = default) =>
        Offline ? throw new HttpRequestException("offline") : Task.FromResult(Info ?? Default(prRef));

    public Task<IReadOnlyList<PrCommit>> GetCommitsAsync(PrRef prRef, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PrCommit>>(Commits);

    public Task<string> GetDiffAsync(PrRef prRef, CancellationToken ct = default) =>
        Diff is null ? throw new BitbucketException("sin diff") : Task.FromResult(Diff);

    /// <summary>Comments already on the PR; null makes reading them fail.</summary>
    public List<PrComment>? Comments { get; set; } = [];

    /// <summary>Comments published through prt.</summary>
    public List<(PrRef Pr, NewComment Comment)> Posted { get; } = [];

    public Task<IReadOnlyList<PrComment>> GetCommentsAsync(PrRef prRef, CancellationToken ct = default) =>
        Comments is null ? throw new BitbucketException("sin comentarios") : Task.FromResult<IReadOnlyList<PrComment>>(Comments.ToList());

    /// <summary>When set, publishing fails with this message (e.g. a read-only token).</summary>
    public string? PostError { get; set; }

    public Task<PrComment> AddCommentAsync(PrRef prRef, NewComment comment, CancellationToken ct = default)
    {
        if (PostError is not null)
            throw new BitbucketException(PostError);
        Posted.Add((prRef, comment));
        var created = new PrComment(1000 + Posted.Count, comment.ParentId, "Vos", comment.Body, DateTimeOffset.UtcNow, comment.Path,
            comment.OldSide ? comment.Line : null, comment.OldSide ? null : comment.Line, $"{prRef.WebUrl}#comment-{1000 + Posted.Count}");
        Comments?.Add(created);
        return Task.FromResult(created);
    }

    public static PullRequestInfo Default(PrRef prRef) => new(
        prRef, "Agregar pagos con QR", "Implementa el flujo de cobro.", "OPEN", "Ana Pérez",
        "feature/qr", "main", "aaaaaaaaaaaa", prRef.WebUrl);
}

internal sealed class FakeUi : IReviewUi, IPanelUi
{
    public List<string> Messages { get; } = [];
    public bool ConfirmAnswer { get; set; } = true;
    public string? Note { get; set; }
    public string? SessionId { get; set; }

    public void Info(string message) => Add("info: " + message);
    public void Warn(string message) => Add("warn: " + message);
    public bool Confirm(string question, bool defaultValue) => ConfirmAnswer;
    public string? AskSessionId(string agent) => SessionId;
    public string? AskNote() => Note;
    public void ReviewerStarted(ReviewerDefinition reviewer, string agent, bool resumed) =>
        Add($"start: {reviewer.Name} {agent} {(resumed ? "resume" : "new")}");
    public void ReviewerFinished(ReviewerOutcome outcome) =>
        Add($"done: {outcome.Reviewer.Name} {outcome.Result.Status}");

    private void Add(string message)
    {
        lock (Messages)
            Messages.Add(message);
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("prt-tests-").FullName;
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
