using System.Net;
using Prt.Core.Agents;
using Prt.Core.Bitbucket;
using Prt.Core.Domain;
using Prt.Core.Review;

namespace Prt.Core.Tests;

internal sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now = _now.AddMinutes(1);
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
        Captured.Add((command, args.ToList(), cwd));
        return Task.FromResult(OnCaptured(args.ToList()));
    }
}

internal sealed class FakeBitbucket : IPullRequestSource
{
    public PullRequestInfo? Info { get; set; }
    public List<PrCommit> Commits { get; set; } = [];
    public bool Offline { get; set; }

    public Task<PullRequestInfo> GetPullRequestAsync(PrRef prRef, CancellationToken ct = default) =>
        Offline ? throw new HttpRequestException("offline") : Task.FromResult(Info ?? Default(prRef));

    public Task<IReadOnlyList<PrCommit>> GetCommitsAsync(PrRef prRef, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PrCommit>>(Commits);

    public static PullRequestInfo Default(PrRef prRef) => new(
        prRef, "Agregar pagos con QR", "Implementa el flujo de cobro.", "OPEN", "Ana Pérez",
        "feature/qr", "main", "aaaaaaaaaaaa", prRef.WebUrl);
}

internal sealed class FakeUi : IReviewUi
{
    public List<string> Messages { get; } = [];
    public bool ConfirmAnswer { get; set; } = true;
    public string? Note { get; set; }
    public string? SessionId { get; set; }

    public void Info(string message) => Messages.Add("info: " + message);
    public void Warn(string message) => Messages.Add("warn: " + message);
    public bool Confirm(string question, bool defaultValue) => ConfirmAnswer;
    public string? AskSessionId(string agent) => SessionId;
    public string? AskNote() => Note;
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
