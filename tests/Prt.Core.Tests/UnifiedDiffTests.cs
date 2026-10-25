using Prt.Core.Panel;

namespace Prt.Core.Tests;

public class UnifiedDiffTests
{
    internal const string Sample =
        """
        diff --git a/src/Pay.cs b/src/Pay.cs
        index 1111111..2222222 100644
        --- a/src/Pay.cs
        +++ b/src/Pay.cs
        @@ -10,4 +10,5 @@ public class Pay
             var id = request.Id;
        -    var sql = "select * from pagos";
        +    var sql = "select * from pagos where id=" + id;
        +    Log(sql);
             return db.Query(sql);
        @@ -40,2 +41,2 @@
        -}
        +} // fin
        \ No newline at end of file
        diff --git a/docs/old.md b/docs/new.md
        similarity index 90%
        rename from docs/old.md
        rename to docs/new.md
        diff --git a/src/Nuevo.cs b/src/Nuevo.cs
        new file mode 100644
        --- /dev/null
        +++ b/src/Nuevo.cs
        @@ -0,0 +1,2 @@
        +class Nuevo {}
        +// <script>
        diff --git a/src/Viejo.cs b/src/Viejo.cs
        deleted file mode 100644
        --- a/src/Viejo.cs
        +++ /dev/null
        @@ -1 +0,0 @@
        -class Viejo {}
        diff --git a/img/logo.png b/img/logo.png
        Binary files a/img/logo.png and b/img/logo.png differ
        """;

    [Fact]
    public void Parses_files_hunks_and_line_numbers()
    {
        var files = UnifiedDiff.Parse(Sample);

        Assert.Equal(["src/Pay.cs", "docs/new.md", "src/Nuevo.cs", "src/Viejo.cs", "img/logo.png"], files.Select(f => f.Path));
        Assert.Equal(
            [DiffFileStatus.Modified, DiffFileStatus.Renamed, DiffFileStatus.Added, DiffFileStatus.Deleted, DiffFileStatus.Binary],
            files.Select(f => f.Status));

        var pay = files[0];
        Assert.Equal((3, 2), (pay.Added, pay.Removed));
        Assert.Equal(2, pay.Hunks.Count);
        var lines = pay.Hunks[0].Lines;
        Assert.Equal(new DiffLine(DiffLineKind.Context, 10, 10, "    var id = request.Id;"), lines[0]);
        Assert.Equal(new DiffLine(DiffLineKind.Removed, 11, null, "    var sql = \"select * from pagos\";"), lines[1]);
        Assert.Equal((DiffLineKind.Added, (int?)11), (lines[2].Kind, lines[2].NewLine));
        Assert.Equal((DiffLineKind.Added, (int?)12), (lines[3].Kind, lines[3].NewLine));
        Assert.Equal((DiffLineKind.Context, (int?)12, (int?)13), (lines[4].Kind, lines[4].OldLine, lines[4].NewLine));
        Assert.Equal(DiffLineKind.Note, pay.Hunks[1].Lines[^1].Kind);

        Assert.Equal("docs/old.md", files[1].OldPath);
        Assert.Equal([1, 2], files[2].Hunks[0].Lines.Select(l => l.NewLine!.Value));
        Assert.Equal("src/Viejo.cs", files[3].OldPath);
    }

    [Fact]
    public void Handles_crlf_and_empty_input()
    {
        Assert.Empty(UnifiedDiff.Parse(""));
        var crlf = UnifiedDiff.Parse(Sample.ReplaceLineEndings("\r\n"));
        Assert.Equal("    var id = request.Id;", crlf[0].Hunks[0].Lines[0].Text);
    }

    [Theory]
    [InlineData("src/Pay.cs", "src/Pay.cs", true)]
    [InlineData("./src/pay.cs", "src/Pay.cs", true)]
    [InlineData("Pay.cs", "src/Pay.cs", true)]
    [InlineData("api/src/Pay.cs", "src/Pay.cs", true)]
    [InlineData("src\\Pay.cs", "src/Pay.cs", true)]
    [InlineData("Pay.cs", "src/OtherPay.cs", false)]
    [InlineData(null, "src/Pay.cs", false)]
    public void Matches_finding_paths(string? finding, string diffPath, bool expected) =>
        Assert.Equal(expected, UnifiedDiff.SamePath(finding, diffPath));
}
