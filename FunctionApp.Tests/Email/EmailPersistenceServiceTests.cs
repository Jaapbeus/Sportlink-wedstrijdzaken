using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

public class EmailPersistenceServiceTests
{
    [Fact]
    public void ResolveClubCode_ZonderProviderWaarde_GooitInvalidOperation()
    {
        var repo = new Mock<IEmailPersistenceRepository>(MockBehavior.Strict);
        var service = new EmailPersistenceService(repo.Object, () => null);

        var act = () => service.ResolveClubCode();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*clubCode*");
    }

    [Fact]
    public async Task LaadUitgeslotenAdressenAsync_LeestMetResolvedClubCode()
    {
        var repo = new Mock<IEmailPersistenceRepository>(MockBehavior.Strict);
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a@x.nl", "b@x.nl" };
        repo.Setup(r => r.GetExcludedEmailAddressesAsync("VRC")).ReturnsAsync(expected);
        var service = new EmailPersistenceService(repo.Object, () => "VRC");

        var result = await service.LaadUitgeslotenAdressenAsync(NullLogger.Instance);

        result.Should().BeEquivalentTo(expected);
        repo.VerifyAll();
    }

    [Fact]
    public async Task DetecteerReplyOpOnsAntwoordAsync_GeeftClubCodeDoorAanRepository()
    {
        var repo = new Mock<IEmailPersistenceRepository>(MockBehavior.Strict);
        var expected = (true, (int?)77, "HerplanVerzoek", "samenvatting");
        repo.Setup(r => r.DetecteerReplyOpOnsAntwoordAsync("conv-1", "VRC", It.IsAny<Microsoft.Extensions.Logging.ILogger>()))
            .ReturnsAsync(expected);
        var service = new EmailPersistenceService(repo.Object, () => "VRC");

        var result = await service.DetecteerReplyOpOnsAntwoordAsync("conv-1", NullLogger.Instance);

        result.Should().Be(expected);
        repo.VerifyAll();
    }

    [Fact]
    public async Task InsertClassificatieCorrectieAsync_GebruiktResolvedClubCode()
    {
        var repo = new Mock<IEmailPersistenceRepository>(MockBehavior.Strict);
        repo.Setup(r => r.InsertClassificatieCorrectieAsync(
                1,
                2,
                "Origineel",
                "Juist",
                "orig",
                "corr",
                "VRC"))
            .Returns(Task.CompletedTask);
        var service = new EmailPersistenceService(repo.Object, () => "VRC");

        await service.InsertClassificatieCorrectieAsync(1, 2, "Origineel", "Juist", "orig", "corr");

        repo.VerifyAll();
    }
}
