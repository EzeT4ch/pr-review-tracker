using System.Globalization;
using System.Text;
using Markdig;
using Prt.Core.Panel;

namespace Prt.Web;

/// <summary>HTML building blocks. Pages are rendered on the server: no JavaScript, one inline stylesheet.</summary>
internal static class Html
{
    // Raw HTML in memos or PR descriptions is shown as text, never rendered.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .Build();

    /// <summary>Escapes only what HTML needs; accents stay as UTF-8 instead of numeric entities.</summary>
    public static string E(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.AsSpan().IndexOfAny("&<>\"'") < 0)
            return text ?? "";
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    public static string Markdown(string? markdown) => Markdig.Markdown.ToHtml(markdown ?? "", Pipeline);

    public static string Url(params string[] segments) =>
        "/" + string.Join('/', segments.Select(Uri.EscapeDataString));

    public static string Date(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string SeverityBadge(Severity severity) =>
        $"<span class=\"sev sev-{severity.ToString().ToLowerInvariant()}\">{E(ReviewStandard.Label(severity))}</span>";

    public static string VerdictBadge(string? verdict)
    {
        if (string.IsNullOrEmpty(verdict))
            return "<span class=\"muted\">—</span>";
        var css = verdict switch
        {
            "BLOQUEADO" => "v-block",
            "CAMBIOS REQUERIDOS" => "v-changes",
            "APROBAR CON CAMBIOS" => "v-comments",
            "APROBAR" => "v-ok",
            _ => "",
        };
        return $"<span class=\"verdict {css}\">{E(verdict)}</span>";
    }

    public static string Counts(RunView view) =>
        string.Concat(new[] { Severity.Blocker, Severity.Major, Severity.Minor, Severity.Info }
            .Select(s => view.Count(s) is var n and > 0
                ? $"<span class=\"sev sev-{s.ToString().ToLowerInvariant()}\" title=\"{E(ReviewStandard.Label(s))}\">{n}</span>"
                : ""));

    /// <summary>Hidden CSRF field every POST form must carry.</summary>
    public static string Csrf(string token) => $"<input type=\"hidden\" name=\"csrf\" value=\"{E(token)}\">";

    public static string Page(string title, string body, int? refreshSeconds = null)
    {
        var sb = new StringBuilder(body.Length + 4096);
        sb.Append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        if (refreshSeconds is { } seconds)
            sb.Append("<meta http-equiv=\"refresh\" content=\"").Append(seconds).Append("\">");
        sb.Append("<title>").Append(E(title)).Append(" · prt</title><style>").Append(Css).Append("</style></head><body>")
            .Append("<header><a class=\"brand\" href=\"/\">prt</a><nav><a href=\"/\">PRs</a><a href=\"/repos\">Repos</a><a href=\"/jobs\">Tareas</a></nav></header><main>")
            .Append(body)
            .Append("</main></body></html>");
        return sb.ToString();
    }

    private const string Css = """
        :root{--bg:#fff;--fg:#1f2328;--muted:#656d76;--line:#d0d7de;--card:#f6f8fa;--link:#0969da;
          --red:#cf222e;--orange:#bc4c00;--yellow:#9a6700;--blue:#0969da;--green:#1a7f37}
        @media (prefers-color-scheme:dark){:root{--bg:#0d1117;--fg:#e6edf3;--muted:#8d96a0;--line:#30363d;--card:#161b22;
          --link:#4493f8;--red:#f85149;--orange:#db6d28;--yellow:#d29922;--blue:#4493f8;--green:#3fb950}}
        *{box-sizing:border-box}body{margin:0;font:14px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif;background:var(--bg);color:var(--fg)}
        header{display:flex;gap:24px;align-items:center;padding:10px 24px;border-bottom:1px solid var(--line)}
        header nav a{margin-right:16px}.brand{font-weight:700;font-size:16px;color:var(--fg)}
        main{max-width:1200px;margin:0 auto;padding:16px 24px 48px}
        a{color:var(--link);text-decoration:none}a:hover{text-decoration:underline}
        h1{font-size:20px;margin:8px 0}h2{font-size:16px;margin:28px 0 8px;padding-bottom:4px;border-bottom:1px solid var(--line)}
        h3{font-size:14px;margin:16px 0 6px}
        table{border-collapse:collapse;width:100%;margin:8px 0}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}
        th{color:var(--muted);font-weight:600;white-space:nowrap}
        code{font:12px ui-monospace,Consolas,monospace;background:var(--card);padding:1px 4px;border-radius:4px;word-break:break-all}
        pre{background:var(--card);padding:10px;border-radius:6px;overflow:auto}
        .muted{color:var(--muted)}.card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:12px 16px;margin:12px 0}
        .row{display:flex;gap:12px;flex-wrap:wrap;align-items:center}
        .sev{display:inline-block;min-width:22px;padding:0 6px;margin-right:4px;border-radius:10px;font-size:12px;font-weight:600;color:#fff;text-align:center}
        .sev-blocker{background:var(--red)}.sev-major{background:var(--orange)}.sev-minor{background:var(--yellow)}.sev-info{background:var(--blue)}
        .verdict{font-weight:700}.v-block{color:var(--red)}.v-changes{color:var(--orange)}.v-comments{color:var(--yellow)}.v-ok{color:var(--green)}
        .tag{display:inline-block;padding:0 8px;margin-right:4px;border:1px solid var(--line);border-radius:10px;font-size:12px}
        details{margin:6px 0}summary{cursor:pointer}
        .memo{border-left:3px solid var(--line);padding:2px 12px;margin:8px 0}.memo-agent_summary{border-color:var(--blue)}.memo-decision{border-color:var(--orange)}
        form.inline{display:inline}input,select,textarea,button{font:inherit;color:inherit;background:var(--bg);border:1px solid var(--line);border-radius:6px;padding:5px 8px}
        textarea{width:100%;min-height:90px}button{cursor:pointer;background:var(--card)}button.primary{background:var(--link);color:#fff;border-color:var(--link)}
        .job{padding:8px 12px;border-radius:6px;margin:12px 0;border:1px solid var(--line)}.job-running{border-color:var(--blue)}.job-failed{border-color:var(--red)}
        .log{font:12px ui-monospace,Consolas,monospace;white-space:pre-wrap;margin:6px 0 0}
        """;
}
