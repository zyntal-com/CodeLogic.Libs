using CL.Common.Web;
using Xunit;

namespace Common.Tests;

/// <summary><see cref="HtmlHelper.Sanitize"/> is an allowlist: nothing executable may survive.</summary>
public sealed class HtmlSanitizeTests
{
    public static TheoryData<string> XssCorpus =>
    [
        "<script>alert(1)</script>",
        "<SCRIPT SRC=//evil/x.js></SCRIPT>",
        "<img src=x onerror=alert(1)>",
        "<img src='x' onerror='alert(1)'>",
        "<p onclick=alert(1)>x</p>",
        "<a href=\"javascript:alert(1)\">x</a>",
        "<a href='JaVaScRiPt:alert(1)'>x</a>",
        "<a href=\"jav&#x09;ascript:alert(1)\">x</a>",
        "<a href=\"&#106;avascript:alert(1)\">x</a>",
        "<a href=\" javascript:alert(1)\">x</a>",
        "<a href=javascript:alert(1)>x</a>",
        "<a href=\"data:text/html;base64,PHNjcmlwdD4=\">x</a>",
        "<a href=\"vbscript:msgbox(1)\">x</a>",
        "<svg onload=alert(1)>",
        "<svg><script>alert(1)</script></svg>",
        "<iframe src=\"https://evil.test\"></iframe>",
        "<object data=\"x.swf\"></object>",
        "<embed src=\"x.swf\">",
        "<form action=\"https://evil.test\"><button>go</button></form>",
        "<div style=\"background:url(javascript:alert(1))\">x</div>",
        "<style>@import 'evil.css';</style>",
        "<b title=\"x\" onmouseover=\"alert(1)\">x</b>",
        "<<script>script>alert(1)<</script>/script>",
        "<scr<script>ipt>alert(1)</script>",
        "<!--<script>alert(1)--><script>alert(1)</script>",
        "<p title='a>b' onclick='alert(1)'>x</p>",
        "<a href=\"https://ok.test\" onclick=\"alert(1)\">x</a>",
        "<base href=\"https://evil.test/\">",
        "<meta http-equiv=\"refresh\" content=\"0;url=https://evil.test\">",
    ];

    [Theory, MemberData(nameof(XssCorpus))]
    public void Xss_vectors_are_neutralized(string input)
    {
        var output = HtmlHelper.Sanitize(input);

        Assert.DoesNotContain("<script", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"<[^>]*\son\w+\s*=", output);
        Assert.DoesNotMatch(@"<(img|svg|iframe|object|embed|form|style|base|meta|button)\b", output);
        Assert.DoesNotContain("style=", output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<p>Hi <strong class=\"x\">there</strong></p>", "<p>Hi <strong>there</strong></p>")]
    [InlineData("<a href=\"https://x.test/?a=1&amp;b=2\" target=\"_blank\">link</a>", "<a href=\"https://x.test/?a=1&amp;b=2\" rel=\"nofollow noopener noreferrer\">link</a>")]
    [InlineData("<a href=\"/local/page\">x</a>", "<a href=\"/local/page\" rel=\"nofollow noopener noreferrer\">x</a>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "<a>x</a>")]
    [InlineData("a <custom>b</custom> c", "a b c")]
    [InlineData("1 < 2 &amp; 3 > 2", "1 &lt; 2 &amp; 3 &gt; 2")]
    [InlineData("line<br/>break", "line<br>break")]
    [InlineData("keep <script>gone()</script>text", "keep text")]
    public void Formatting_is_kept_and_rebuilt(string input, string expected) =>
        Assert.Equal(expected, HtmlHelper.Sanitize(input));

    [Fact]
    public void Empty_input_gives_empty_output()
    {
        Assert.Equal("", HtmlHelper.Sanitize(""));
        Assert.Equal("", HtmlHelper.Sanitize(null!));
    }
}
