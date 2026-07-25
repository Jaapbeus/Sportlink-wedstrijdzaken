using FluentAssertions;
using FunctionApp.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

public class EmailPersistenceServiceTests
{
    [Fact]
    public void ResolveClubCode_ZonderProviderWaarde_GooitInvalidOperation()
    {
        var repo = new FakeEmailPersistenceRepository();
        var service = new EmailPersistenceService(repo, () => null);

        var act = () => service.ResolveClubCode();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*clubCode*");
    }

    [Fact]
    public async Task LaadUitgeslotenAdressenAsync_LeestMetResolvedClubCode()
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a@x.nl", "b@x.nl" };
        var repo = new FakeEmailPersistenceRepository();
        foreach (var address in expected)
            repo.ExcludedAddressesToReturn.Add(address);
        var service = new EmailPersistenceService(repo, () => "VRC");

        var result = await service.LaadUitgeslotenAdressenAsync(NullLogger.Instance);

        result.Should().BeEquivalentTo(expected);
        repo.LastExcludedClubCode.Should().Be("VRC");
    }

    [Fact]
    public async Task DetecteerReplyOpOnsAntwoordAsync_GeeftClubCodeDoorAanRepository()
    {
        var expected = (true, (int?)77, "HerplanVerzoek", "samenvatting");
        var repo = new FakeEmailPersistenceRepository { DetecteerResult = expected };
        var service = new EmailPersistenceService(repo, () => "VRC");

        var result = await service.DetecteerReplyOpOnsAntwoordAsync("conv-1", NullLogger.Instance);

        result.Should().Be(expected);
        repo.LastDetecteerCall.Should().Be(("conv-1", "VRC"));
    }

    [Fact]
    public async Task InsertClassificatieCorrectieAsync_GebruiktResolvedClubCode()
    {
        var repo = new FakeEmailPersistenceRepository();
        var service = new EmailPersistenceService(repo, () => "VRC");

        await service.InsertClassificatieCorrectieAsync(1, 2, "Origineel", "Juist", "orig", "corr");

        repo.LastCorrectieCall.Should().Be((1, 2, "Origineel", "Juist", "orig", "corr", "VRC"));
    }
}
