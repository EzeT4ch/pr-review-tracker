using System.Net;
using Prt.Core.Bitbucket;
using Prt.Core.Config;
using Prt.Core.Domain;

namespace Prt.Core.Tests;

public class BitbucketClientTests
{
    private const string PrJson =
        """
        {
          "id": 42, "title": "Pagos QR", "description": "Implementa cobro", "state": "OPEN",
          "author": { "display_name": "Ana Pérez", "nickname": "ana" },
          "source": { "branch": { "name": "feature/qr" }, "commit": { "hash": "abc123def456" } },
          "destination": { "branch": { "name": "main" } },
          "links": { "html": { "href": "https://bitbucket.org/acme/api/pull-requests/42" } }
        }
        """;

    private static readonly PrRef Ref = new("acme", "api", 42);

    [Fact]
    public async Task Reads_pull_request_with_basic_auth()
    {
        var handler = new StubHandler(_ => StubHandler.Json(PrJson));
        var client = new BitbucketClient(new HttpClient(handler),
            new BitbucketSettings { Username = "ana@acme.com", ApiToken = "secret" });

        var info = await client.GetPullRequestAsync(Ref);

        var request = handler.Requests.Single();
        Assert.Equal("https://api.bitbucket.org/2.0/repositories/acme/api/pullrequests/42", request.RequestUri!.ToString());
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal("Pagos QR", info.Title);
        Assert.Equal("Ana Pérez", info.Author);
        Assert.Equal("feature/qr", info.SourceBranch);
        Assert.Equal("main", info.DestinationBranch);
        Assert.Equal("abc123def456", info.HeadCommit);
    }

    [Fact]
    public async Task Follows_commit_pagination()
    {
        var handler = new StubHandler(request => request.RequestUri!.Query.Contains("page=2")
            ? StubHandler.Json("""{"values":[{"hash":"c1","message":"primero"}]}""")
            : StubHandler.Json("""
                {"values":[{"hash":"c3","message":"tercero\n\ncuerpo","date":"2026-03-10T10:00:00+00:00"},{"hash":"c2","message":"segundo"}],
                 "next":"https://api.bitbucket.org/2.0/repositories/acme/api/pullrequests/42/commits?page=2"}
                """));
        var client = new BitbucketClient(new HttpClient(handler), new BitbucketSettings());

        var commits = await client.GetCommitsAsync(Ref);

        Assert.Equal(["c3", "c2", "c1"], commits.Select(c => c.Hash));
        Assert.Equal("tercero", commits[0].Subject);
        Assert.Null(handler.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task Explains_authentication_failures()
    {
        var client = new BitbucketClient(
            new HttpClient(new StubHandler(_ => StubHandler.Json("{}", HttpStatusCode.Unauthorized))), new BitbucketSettings());

        var ex = await Assert.ThrowsAsync<BitbucketException>(() => client.GetPullRequestAsync(Ref));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        Assert.Contains("API token", ex.Message);
    }

    [Fact]
    public void Compares_short_and_full_hashes()
    {
        Assert.True(CommitHashes.Same("abc123def456", "abc123def4567890"));
        Assert.False(CommitHashes.Same("abc", null));
        PrCommit[] commits = [new("c3full", "", null), new("c2full", "", null), new("c1full", "", null)];
        Assert.Equal(["c3full"], CommitHashes.Since(commits, "c2")!.Select(c => c.Hash));
        Assert.Null(CommitHashes.Since(commits, "zzz"));
    }

    [Fact]
    public async Task Diff_follows_redirects_keeping_credentials()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/pullrequests/42/diff"))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("/2.0/repositories/acme/api/diff/acme/api:abc..def?from_pullrequest_id=42", UriKind.Relative);
                return redirect;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("diff --git a/x b/x\n") };
        });
        var client = new BitbucketClient(new HttpClient(handler), new BitbucketSettings { Username = "u", ApiToken = "t" });

        var diff = await client.GetDiffAsync(Ref);

        Assert.StartsWith("diff --git", diff);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("api.bitbucket.org", handler.Requests[1].RequestUri!.Host);
        Assert.Equal("Basic", handler.Requests[1].Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task Reads_comments_with_their_line_and_thread()
    {
        var handler = new StubHandler(request => request.RequestUri!.Query.Contains("page=2")
            ? StubHandler.Json("""{"values":[{"id":3,"content":{"raw":"general"},"user":{"display_name":"Bruno"},"created_on":"2026-03-10T12:00:00+00:00"}]}""")
            : StubHandler.Json("""
                {"values":[
                  {"id":1,"content":{"raw":"¿y el null?"},"user":{"display_name":"Ana"},"created_on":"2026-03-10T10:00:00+00:00",
                   "inline":{"path":"src/Pay.cs","from":null,"to":12},"links":{"html":{"href":"https://bitbucket.org/acme/api/pull-requests/42#comment-1"}}},
                  {"id":2,"content":{"raw":"ya está"},"user":{"nickname":"bruno"},"created_on":"2026-03-10T11:00:00+00:00","parent":{"id":1},
                   "inline":{"path":"src/Pay.cs","from":7,"to":null}},
                  {"id":9,"content":{"raw":""},"deleted":true,"created_on":"2026-03-10T11:30:00+00:00"}],
                 "next":"https://api.bitbucket.org/2.0/repositories/acme/api/pullrequests/42/comments?page=2"}
                """));
        var client = new BitbucketClient(new HttpClient(handler), new BitbucketSettings());

        var comments = await client.GetCommentsAsync(Ref);

        Assert.Equal([1L, 2L, 3L], comments.Select(c => c.Id));
        Assert.Equal(("src/Pay.cs", (int?)null, (int?)12, "Ana"), (comments[0].Path, comments[0].From, comments[0].To, comments[0].Author));
        Assert.Equal("https://bitbucket.org/acme/api/pull-requests/42#comment-1", comments[0].Url);
        Assert.Equal((1L, (int?)7, "bruno"), (comments[1].ParentId!.Value, comments[1].From, comments[1].Author));
        Assert.False(comments[2].IsInline);
    }

    [Theory]
    [InlineData(false, 12, null, """{"content":{"raw":"ojo"},"inline":{"path":"src/Pay.cs","to":12}}""")]
    [InlineData(true, 7, null, """{"content":{"raw":"ojo"},"inline":{"path":"src/Pay.cs","from":7}}""")]
    [InlineData(false, null, null, """{"content":{"raw":"ojo"},"inline":{"path":"src/Pay.cs"}}""")]
    [InlineData(false, null, 5L, """{"content":{"raw":"ojo"},"inline":{"path":"src/Pay.cs"},"parent":{"id":5}}""")]
    public async Task Posts_comments_on_a_line_a_file_or_as_replies(bool oldSide, int? line, long? parent, string expected)
    {
        string? sent = null;
        var handler = new StubHandler(request =>
        {
            sent = request.Content!.ReadAsStringAsync().Result;
            return StubHandler.Json("""{"id":77,"content":{"raw":"ojo"},"user":{"display_name":"Vos"},"created_on":"2026-03-10T10:00:00+00:00"}""", HttpStatusCode.Created);
        });
        var client = new BitbucketClient(new HttpClient(handler), new BitbucketSettings { Username = "u", ApiToken = "t" });

        var created = await client.AddCommentAsync(Ref, new NewComment(" ojo ", "src/Pay.cs", line, oldSide, parent));

        Assert.Equal(HttpMethod.Post, handler.Requests.Single().Method);
        Assert.Equal("https://api.bitbucket.org/2.0/repositories/acme/api/pullrequests/42/comments", handler.Requests.Single().RequestUri!.ToString());
        Assert.Equal("Basic", handler.Requests.Single().Headers.Authorization!.Scheme);
        Assert.Equal(expected, sent);
        Assert.Equal(77, created.Id);
    }

    [Fact]
    public async Task Explains_that_commenting_needs_write_access()
    {
        var client = new BitbucketClient(new HttpClient(new StubHandler(_ => StubHandler.Json("{}", HttpStatusCode.Forbidden))), new BitbucketSettings());

        var ex = await Assert.ThrowsAsync<BitbucketException>(() => client.AddCommentAsync(Ref, new NewComment("x")));

        Assert.Contains("escritura en pull requests", ex.Message);
    }
}
