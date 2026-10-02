using AwesomeAssertions;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

public class DeelHtmlHelperTests
{
    [Fact]
    public void ZonderScript_VerwijdertScriptElement()
        => DeelHtmlHelper.ZonderScript("<p>a</p><script>alert(1)</script><p>b</p>").Should().Be("<p>a</p><p>b</p>");

    [Fact]
    public void ZonderScript_VerwijdertScriptMetAttributenMeerregeligEnHoofdletters()
        => DeelHtmlHelper.ZonderScript("x<SCRIPT type=\"text/javascript\">\nvar a=1;\n</Script>y").Should().Be("xy");

    [Fact]
    public void ZonderScript_VerwijdertMeerdereScripts()
        => DeelHtmlHelper.ZonderScript("<script>1</script>a<script src=\"x.js\"></script>").Should().Be("a");

    [Fact]
    public void ZonderScript_LaatHtmlZonderScriptOngemoeid()
        => DeelHtmlHelper.ZonderScript("<b>Team &lt;x&gt;</b>").Should().Be("<b>Team &lt;x&gt;</b>");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ZonderScript_LegeInvoerBlijftOngewijzigd(string? invoer)
        => DeelHtmlHelper.ZonderScript(invoer).Should().Be(invoer);
}
