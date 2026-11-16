using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

public class EmailRecordContractTests
{
    [Fact]
    public void ClubAppSettingsSnapshot_BewaartPubliekeRecordvormEnOptioneleDefaults()
    {
        var type = typeof(ClubAppSettingsSnapshot);
        type.FullName.Should().Be("Planner.Shared.Email.ClubAppSettingsSnapshot");
        type.IsPublic.Should().BeTrue();
        type.IsSealed.Should().BeTrue();
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(property => property.Name)
            .Should().Equal(
                "PlannerAfzenderNaam", "CoordinatorNaam", "CoordinatorFunctie", "EmailVoetnoot",
                "HerplanDeadlineDagen", "KnvbPdfBijlageIngeschakeld", "KnvbStandaardRegio", "EmailReviewModusActief");
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(property => property.PropertyType)
            .Should().Equal(typeof(string), typeof(string), typeof(string), typeof(string), typeof(int?), typeof(bool?), typeof(string), typeof(bool));

        var parameters = type.GetConstructors().Single().GetParameters();
        parameters.Select(parameter => parameter.HasDefaultValue)
            .Should().Equal(false, false, false, false, false, true, true, true);
        parameters[5].DefaultValue.Should().BeNull();
        parameters[6].DefaultValue.Should().BeNull();
        parameters[7].DefaultValue.Should().Be(true);
        new NullabilityInfoContext().Create(type.GetProperty("PlannerAfzenderNaam")!).ReadState
            .Should().Be(NullabilityState.Nullable);

        var record = new ClubAppSettingsSnapshot("Naam", "Coördinator", "Functie", "Voetnoot", 7);
        record.KnvbPdfBijlageIngeschakeld.Should().BeNull();
        record.KnvbStandaardRegio.Should().BeNull();
        record.EmailReviewModusActief.Should().BeTrue();
        (record with { EmailReviewModusActief = false }).EmailReviewModusActief.Should().BeFalse();
        (record with { HerplanDeadlineDagen = 8 }).HerplanDeadlineDagen.Should().Be(8);
        JsonSerializer.Serialize(record).Should().Contain("\"HerplanDeadlineDagen\":7");
    }

    [Fact]
    public void EmailTemplate_BewaartRecordContractEnInterface()
    {
        var type = typeof(EmailTemplate);
        type.FullName.Should().Be("Planner.Shared.Email.EmailTemplate");
        type.IsPublic.Should().BeTrue();
        type.IsSealed.Should().BeFalse();
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(property => property.Name)
            .Should().Equal("Key", "Onderwerp", "Body");
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(property => property.PropertyType)
            .Should().Equal(typeof(string), typeof(string), typeof(string));
        typeof(IAntwoordSjabloon).IsAssignableFrom(type).Should().BeTrue();
        new NullabilityInfoContext().Create(type.GetProperty("Body")!).ReadState
            .Should().Be(NullabilityState.NotNull);

        var record = new EmailTemplate("key", "onderwerp", "body");
        record.Should().Be(new EmailTemplate("key", "onderwerp", "body"));
        (record with { Body = "nieuw" }).Body.Should().Be("nieuw");
        JsonSerializer.Serialize(record).Should().Be("{\"Key\":\"key\",\"Onderwerp\":\"onderwerp\",\"Body\":\"body\"}");
    }

    [Fact]
    public void EmailVerwerkingStand_BewaartPubliekeVerzegeldeRecordvormEnDefault()
    {
        var type = typeof(EmailVerwerkingStand);
        type.FullName.Should().Be("Planner.Shared.Email.EmailVerwerkingStand");
        type.IsPublic.Should().BeTrue();
        type.IsSealed.Should().BeTrue();
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(property => property.Name)
            .Should().Equal("VerwerkingId", "Status", "Pogingen", "AntwoordVerstuurd", "VerzendPogingOnbeslist");
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(property => property.PropertyType)
            .Should().Equal(typeof(int), typeof(string), typeof(int), typeof(bool), typeof(bool));
        new NullabilityInfoContext().Create(type.GetProperty("Status")!).ReadState
            .Should().Be(NullabilityState.NotNull);
        var laatsteParameter = type.GetConstructors().Single().GetParameters().Last();
        laatsteParameter.HasDefaultValue.Should().BeTrue();
        laatsteParameter.DefaultValue.Should().Be(false);

        var record = new EmailVerwerkingStand(12, "InBehandeling", 2, false);
        record.VerzendPogingOnbeslist.Should().BeFalse();
        record.Should().Be(new EmailVerwerkingStand(12, "InBehandeling", 2, false, false));
        (record with { VerzendPogingOnbeslist = true }).VerzendPogingOnbeslist.Should().BeTrue();
        JsonSerializer.Serialize(record).Should().Contain("\"VerzendPogingOnbeslist\":false");
    }
}
