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
}
