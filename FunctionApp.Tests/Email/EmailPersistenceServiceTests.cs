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
        var service = new EmailPersistenceService(repo, () => "ALLSTARS");

        var result = await service.LaadUitgeslotenAdressenAsync(NullLogger.Instance);

        result.Should().BeEquivalentTo(expected);
        repo.LastExcludedClubCode.Should().Be("ALLSTARS");
    }

    [Fact]
    public async Task DetecteerReplyOpOnsAntwoordAsync_GeeftClubCodeDoorAanRepository()
    {
        var expected = (true, (int?)77, "HerplanVerzoek", "samenvatting");
        var repo = new FakeEmailPersistenceRepository { DetecteerResult = expected };
        var service = new EmailPersistenceService(repo, () => "ALLSTARS");

        var result = await service.DetecteerReplyOpOnsAntwoordAsync("conv-1", NullLogger.Instance);

        result.Should().Be(expected);
        repo.LastDetecteerCall.Should().Be(("conv-1", "ALLSTARS"));
    }

    /// <summary>
    /// De guard leest de stand op MessageId en bewust NIET op ClubCode: UQ_EmailVerwerking_MessageId
    /// is een globale unique constraint. Zou er wel op club gefilterd worden, dan bleef een rij van
    /// een andere club onzichtbaar en klapte de daaropvolgende INSERT op die constraint — waarmee het
    /// bericht eeuwig blijft falen. (#712)
    /// </summary>
    [Fact]
    public async Task HaalVerwerkingStandOpAsync_LeestOpMessageId_ZonderClubFilter()
    {
        var verwacht = new EmailVerwerkingStand(
            VerwerkingId: 7, Status: "Fout", Pogingen: 2, AntwoordVerstuurd: false);
        var repo = new FakeEmailPersistenceRepository { StandToReturn = verwacht };
        var service = new EmailPersistenceService(repo, () => "ALLSTARS");

        var result = await service.HaalVerwerkingStandOpAsync("msg-1");

        result.Should().Be(verwacht);
        repo.LastStandMessageId.Should().Be("msg-1");
    }

    [Fact]
    public async Task HaalVerwerkingStandOpAsync_OnbekendBericht_GeeftNull()
    {
        var repo = new FakeEmailPersistenceRepository();
        var service = new EmailPersistenceService(repo, () => "ALLSTARS");

        (await service.HaalVerwerkingStandOpAsync("onbekend")).Should().BeNull();
    }

    [Fact]
    public async Task InsertClassificatieCorrectieAsync_GebruiktResolvedClubCode()
    {
        var repo = new FakeEmailPersistenceRepository();
        var service = new EmailPersistenceService(repo, () => "ALLSTARS");

        await service.InsertClassificatieCorrectieAsync(1, 2, "Origineel", "Juist", "orig", "corr");

        repo.LastCorrectieCall.Should().Be((1, 2, "Origineel", "Juist", "orig", "corr", "ALLSTARS"));
    }
}
