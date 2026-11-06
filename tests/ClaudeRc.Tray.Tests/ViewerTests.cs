using System.Text;
using ClaudeRc.Tray.Viewer;
using Microsoft.Web.WebView2.Core;

namespace ClaudeRc.Tray.Tests;

public class ViewerProtocolTests
{
    [Fact]
    public void Parses_the_pages_messages()
    {
        Assert.Equal(new PageReady(120, 40), ViewerProtocol.Parse("""{"type":"ready","cols":120,"rows":40}"""));
        Assert.Equal(new PageInput("y\r"), ViewerProtocol.Parse("""{"type":"input","data":"y\r"}"""));
        Assert.Equal(new PageResize(80, 24), ViewerProtocol.Parse("""{"type":"resize","cols":80,"rows":24}"""));
        Assert.Equal(new PageCopy("a b"), ViewerProtocol.Parse("""{"type":"copy","text":"a b"}"""));
    }

    [Fact]
    public void The_type_may_come_after_the_other_fields()
    {
        Assert.Equal(new PageReady(1, 2), ViewerProtocol.Parse("""{"cols":1,"rows":2,"type":"ready"}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("[]")]
    [InlineData("\"input\"")]
    [InlineData("{}")]
    [InlineData("""{"type":"bogus"}""")]
    [InlineData("""{"type":"ready"}""")]
    [InlineData("""{"type":"ready","cols":"1","rows":2}""")]
    [InlineData("""{"type":"input","data":5}""")]
    [InlineData("""{"type":"input","data":null}""")]
    [InlineData("""{"type":"input","data":"\ud800"}""")]
    public void Anything_else_is_null(string json)
    {
        Assert.Null(ViewerProtocol.Parse(json));
    }

    [Fact]
    public void Output_carries_the_bytes_as_base64()
    {
        Assert.Equal("""{"type":"output","data":"4oI="}""", ViewerProtocol.Encode(new ViewerOutput([0xE2, 0x82])));
        Assert.Equal("""{"type":"output","data":""}""", ViewerProtocol.Encode(new ViewerOutput([])));
    }

    [Fact]
    public void Status_and_focus()
    {
        Assert.Equal("""{"type":"status","text":null,"level":"info"}""", ViewerProtocol.Encode(new ViewerStatus(null, "info")));
        Assert.Equal("""{"type":"status","text":"Host not running","level":"error"}""",
            ViewerProtocol.Encode(new ViewerStatus("Host not running", "error")));
        Assert.Equal("""{"type":"focus"}""", ViewerProtocol.Encode(new ViewerFocus()));
    }
}

public class ViewerContentTests
{
    [Theory]
    [InlineData("/index.html", "text/html; charset=utf-8")]
    [InlineData("/viewer.js", "text/javascript; charset=utf-8")]
    [InlineData("/viewer.css", "text/css; charset=utf-8")]
    [InlineData("/xterm/xterm.js", "text/javascript; charset=utf-8")]
    [InlineData("/xterm/xterm.css", "text/css; charset=utf-8")]
    [InlineData("/addon-fit/addon-fit.js", "text/javascript; charset=utf-8")]
    [InlineData("/xterm/LICENSE", "text/plain; charset=utf-8")]
    [InlineData("/addon-fit/LICENSE", "text/plain; charset=utf-8")]
    public void Serves_the_pages_files_from_the_assembly(string path, string type)
    {
        var file = ViewerContent.TryGet(ViewerContent.Origin + path);

        Assert.NotNull(file);
        Assert.Equal(type, file.ContentType);
        Assert.NotEmpty(file.Content);
    }

    [Fact]
    public void The_start_page_loads_the_vendored_scripts()
    {
        var html = Encoding.UTF8.GetString(ViewerContent.TryGet(ViewerContent.StartUrl)!.Content);

        Assert.Contains("src=\"xterm/xterm.js\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"addon-fit/addon-fit.js\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"viewer.js\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/index.html")]
    [InlineData("/viewer.js")]
    [InlineData("/viewer.css")]
    public void Our_own_files_reach_nothing_on_the_network(string path)
    {
        var text = Encoding.UTF8.GetString(ViewerContent.TryGet(ViewerContent.Origin + path)!.Content);

        Assert.DoesNotContain("http:", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https:", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("//cdn", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ViewerContent.StartUrl + "?x=1")]
    [InlineData(ViewerContent.StartUrl + "#top")]
    public void Query_and_fragment_are_ignored(string url)
    {
        Assert.NotNull(ViewerContent.TryGet(url));
    }

    [Theory]
    [InlineData("https://example.com/index.html")]
    [InlineData("http://viewer.claude-rc.example/index.html")]
    [InlineData("https://viewer.claude-rc.example.evil.com/index.html")]
    [InlineData(ViewerContent.Origin + "/")]
    [InlineData(ViewerContent.Origin + "/missing.js")]
    [InlineData(ViewerContent.Origin + "/xterm/../../secret.txt")]
    [InlineData(ViewerContent.Origin + "/xterm/xterm.js.map")]
    [InlineData("not a url")]
    public void Anything_else_is_not_found(string url)
    {
        Assert.Null(ViewerContent.TryGet(url));
    }

    [Fact]
    public void The_origin_is_a_reserved_name()
    {
        Assert.EndsWith(".example", new Uri(ViewerContent.Origin).Host, StringComparison.Ordinal);
    }
}

public class ViewerRuntimeTests
{
    [Fact]
    public void A_missing_runtime_says_what_to_install()
    {
        var text = ViewerRuntime.Describe(new WebView2RuntimeNotFoundException());

        Assert.Contains("WebView2 Runtime", text, StringComparison.Ordinal);
        Assert.Contains("not installed", text, StringComparison.Ordinal);
        Assert.Contains(ViewerRuntime.DownloadUrl, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_errors_are_shown_as_they_are()
    {
        var text = ViewerRuntime.Describe(new UnauthorizedAccessException("access denied to the data folder"));

        Assert.Contains("access denied to the data folder", text, StringComparison.Ordinal);
        Assert.DoesNotContain("not installed", text, StringComparison.Ordinal);
    }
}
