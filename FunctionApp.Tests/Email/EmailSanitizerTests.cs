using FluentAssertions;
using SportlinkFunction;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Tests voor EmailSanitizer.SanitizeFoutMelding — regressietest voor #420/#463.
/// </summary>
public class EmailSanitizerTests
{
    [Fact]
    public void SanitizeFoutMelding_EmailInMessage_WordtGemaskeerd()
    {
        var result = EmailSanitizer.SanitizeFoutMelding("Fout voor user@example.com");
        result.Should().NotContain("user@example.com");
        result.Should().Contain("[e-mail]");
    }

    [Fact]
    public void SanitizeFoutMelding_MeerdereEmailsInMessage_WordenAlleMaskeerd()
    {
        var result = EmailSanitizer.SanitizeFoutMelding("Van a@x.nl naar b@y.nl: fout");
        result.Should().NotContain("a@x.nl");
        result.Should().NotContain("b@y.nl");
        result.Should().Contain("[e-mail]");
    }

    [Fact]
    public void SanitizeFoutMelding_GeenEmail_OngewijzigdTeruggegeven()
    {
        var result = EmailSanitizer.SanitizeFoutMelding("Gewone foutmelding zonder e-mailadres");
        result.Should().Be("Gewone foutmelding zonder e-mailadres");
    }

    [Fact]
    public void SanitizeFoutMelding_LangeBoodschap_WordtAfgekapt()
    {
        var lang = new string('x', 300);
        var result = EmailSanitizer.SanitizeFoutMelding(lang);
        result.Length.Should().BeLessOrEqualTo(203); // 200 tekens + "…"
        result.Should().EndWith("…");
    }

    [Fact]
    public void SanitizeFoutMelding_KorteBoodschap_WordtNietAfgekapt()
    {
        var kort = "Korte fout";
        var result = EmailSanitizer.SanitizeFoutMelding(kort);
        result.Should().Be(kort);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizeFoutMelding_LegOfNull_GeeftOnbekendefout(string? input)
    {
        var result = EmailSanitizer.SanitizeFoutMelding(input!);
        result.Should().Be("Onbekende fout");
    }
}
