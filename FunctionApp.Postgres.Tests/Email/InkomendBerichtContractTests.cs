using AwesomeAssertions;
using FunctionApp.Postgres.Email;
using Newtonsoft.Json;
using System.Reflection;
using System.Text.Json;
using Xunit;
using StjSerializer = System.Text.Json.JsonSerializer;

namespace FunctionApp.Postgres.Tests.Email;

public sealed class InkomendBerichtContractTests
{
    private const string DefaultNewtonsoft = "{\"MessageId\":\"\",\"ConversationId\":null,\"Afzender\":\"\",\"AfzenderNaam\":\"\",\"Onderwerp\":\"\",\"OntvangstDatum\":\"0001-01-01T00:00:00\",\"Body\":null,\"InternetHeaders\":[]}";
    private const string DefaultSystemTextJson = "{\"MessageId\":\"\",\"ConversationId\":null,\"Afzender\":\"\",\"AfzenderNaam\":\"\",\"Onderwerp\":\"\",\"OntvangstDatum\":\"0001-01-01T00:00:00\",\"Body\":null,\"InternetHeaders\":[]}";
    private const string DefaultSystemTextJsonWeb = "{\"messageId\":\"\",\"conversationId\":null,\"afzender\":\"\",\"afzenderNaam\":\"\",\"onderwerp\":\"\",\"ontvangstDatum\":\"0001-01-01T00:00:00\",\"body\":null,\"internetHeaders\":[]}";
    private const string PopulatedNewtonsoft = "{\"MessageId\":\"msg-1937\",\"ConversationId\":\"conversation-1937\",\"Afzender\":\"trainer@voorbeeld.nl\",\"AfzenderNaam\":\"Jan de Vries\",\"Onderwerp\":\"Oefenwedstrijd\",\"OntvangstDatum\":\"2026-11-14T09:08:07Z\",\"Body\":\"synthetische inhoud\",\"InternetHeaders\":[{\"Key\":\"Auto-Submitted\",\"Value\":\"auto-generated\"},{\"Key\":\"X-Test\",\"Value\":\"waarde\"}]}";
    private const string PopulatedSystemTextJson = PopulatedNewtonsoft;
    private const string PopulatedSystemTextJsonWeb = "{\"messageId\":\"msg-1937\",\"conversationId\":\"conversation-1937\",\"afzender\":\"trainer@voorbeeld.nl\",\"afzenderNaam\":\"Jan de Vries\",\"onderwerp\":\"Oefenwedstrijd\",\"ontvangstDatum\":\"2026-11-14T09:08:07Z\",\"body\":\"synthetische inhoud\",\"internetHeaders\":[{\"key\":\"Auto-Submitted\",\"value\":\"auto-generated\"},{\"key\":\"X-Test\",\"value\":\"waarde\"}]}";

    [Theory]
    [InlineData("Newtonsoft", DefaultNewtonsoft, PopulatedNewtonsoft)]
    [InlineData("SystemTextJson", DefaultSystemTextJson, PopulatedSystemTextJson)]
    [InlineData("SystemTextJsonWeb", DefaultSystemTextJsonWeb, PopulatedSystemTextJsonWeb)]
    public void Serialize_KeepGoldenPropertyOrderAndValues(string serializer, string defaultJson, string populatedJson)
    {
        Serialize(serializer, new InkomendBericht()).Should().Be(defaultJson);
        Serialize(serializer, MaakGevuldBericht()).Should().Be(populatedJson);
    }

    [Theory]
    [InlineData("Newtonsoft", DefaultNewtonsoft, "{\"MessageId\":\"\",\"ConversationId\":null,\"Afzender\":\"\",\"AfzenderNaam\":\"\",\"Onderwerp\":\"\",\"OntvangstDatum\":\"0001-01-01T00:00:00\",\"Body\":null,\"InternetHeaders\":[]}")]
    [InlineData("SystemTextJson", DefaultSystemTextJson, "{\"MessageId\":\"\",\"ConversationId\":null,\"Afzender\":\"\",\"AfzenderNaam\":\"\",\"Onderwerp\":\"\",\"OntvangstDatum\":\"0001-01-01T00:00:00\",\"Body\":null,\"InternetHeaders\":[]}")]
    [InlineData("SystemTextJsonWeb", DefaultSystemTextJsonWeb, "{\"messageId\":\"\",\"conversationId\":null,\"afzender\":\"\",\"afzenderNaam\":\"\",\"onderwerp\":\"\",\"ontvangstDatum\":\"0001-01-01T00:00:00\",\"body\":null,\"internetHeaders\":[]}")]
    public void Deserialize_MissingFieldsKeepNull_AndExplicitNullRemainsNull(
        string serializer, string missingFieldsJson, string explicitNullJson)
    {
        var missing = Deserialize(serializer, "{}");
        missing.MessageId.Should().Be("");
        missing.ConversationId.Should().BeNull();
        missing.Body.Should().BeNull();
        missing.InternetHeaders.Should().BeEmpty();

        var explicitNull = Deserialize(serializer, explicitNullJson);
        explicitNull.ConversationId.Should().BeNull();
        explicitNull.Body.Should().BeNull();

        Serialize(serializer, missing).Should().Be(missingFieldsJson);
        Serialize(serializer, explicitNull).Should().Be(explicitNullJson);
    }

    [Fact]
    public void ConcreteContract_HasOriginalSealednessNullabilityAndPublicSetters()
    {
        typeof(InkomendBericht).IsSealed.Should().BeTrue();
        typeof(InkomendBericht).FullName.Should().Be("FunctionApp.Postgres.Email.InkomendBericht");
        typeof(global::Planner.Shared.Email.IInkomendBericht).IsAssignableFrom(typeof(InkomendBericht)).Should().BeTrue();
        var nullability = new NullabilityInfoContext();
        var expected = new Dictionary<string, (Type Type, NullabilityState State)>
        {
            [nameof(InkomendBericht.MessageId)] = (typeof(string), NullabilityState.NotNull),
            [nameof(InkomendBericht.ConversationId)] = (typeof(string), NullabilityState.Nullable),
            [nameof(InkomendBericht.Afzender)] = (typeof(string), NullabilityState.NotNull),
            [nameof(InkomendBericht.AfzenderNaam)] = (typeof(string), NullabilityState.NotNull),
            [nameof(InkomendBericht.Onderwerp)] = (typeof(string), NullabilityState.NotNull),
            [nameof(InkomendBericht.OntvangstDatum)] = (typeof(DateTime), NullabilityState.NotNull),
            [nameof(InkomendBericht.Body)] = (typeof(string), NullabilityState.Nullable),
            [nameof(InkomendBericht.InternetHeaders)] = (typeof(IReadOnlyList<KeyValuePair<string, string>>), NullabilityState.NotNull)
        };

        foreach (var (name, (type, state)) in expected)
        {
            var property = typeof(InkomendBericht).GetProperty(name)!;
            property.PropertyType.Should().Be(type);
            property.SetMethod.Should().NotBeNull();
            property.SetMethod!.IsPublic.Should().BeTrue();
            nullability.Create(property).WriteState.Should().Be(state);
            nullability.Create(property).ReadState.Should().Be(state);
        }
    }

    private static InkomendBericht MaakGevuldBericht() => new()
    {
        MessageId = "msg-1937",
        ConversationId = "conversation-1937",
        Afzender = "trainer@voorbeeld.nl",
        AfzenderNaam = "Jan de Vries",
        Onderwerp = "Oefenwedstrijd",
        OntvangstDatum = new DateTime(2026, 11, 14, 9, 8, 7, DateTimeKind.Utc),
        Body = "synthetische inhoud",
        InternetHeaders =
        [
            new("Auto-Submitted", "auto-generated"),
            new("X-Test", "waarde")
        ]
    };

    private static string Serialize(string serializer, InkomendBericht bericht) => serializer switch
    {
        "Newtonsoft" => JsonConvert.SerializeObject(bericht),
        "SystemTextJson" => StjSerializer.Serialize(bericht),
        "SystemTextJsonWeb" => StjSerializer.Serialize(bericht, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        _ => throw new ArgumentOutOfRangeException(nameof(serializer))
    };

    private static InkomendBericht Deserialize(string serializer, string json) => serializer switch
    {
        "Newtonsoft" => JsonConvert.DeserializeObject<InkomendBericht>(json)!,
        "SystemTextJson" => StjSerializer.Deserialize<InkomendBericht>(json)!,
        "SystemTextJsonWeb" => StjSerializer.Deserialize<InkomendBericht>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!,
        _ => throw new ArgumentOutOfRangeException(nameof(serializer))
    };
}
