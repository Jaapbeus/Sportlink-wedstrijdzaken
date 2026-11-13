using AwesomeAssertions;
using Planner.Shared.Integrations.SportlinkClub;
using Planner.Shared.Planning;
using Xunit;

namespace Planner.Shared.Tests.Planning;

/// <summary>#1915: de tabel met veldplanner-blokken wordt per planningsaanvraag bijgewerkt op sleutel en hash.</summary>
public class VeldplannerBlokSynchronisatieTests
{
    private static readonly DateOnly Dag = new(2026, 11, 14);
    private static readonly DateOnly Vandaag = new(2026, 11, 13);

    private static SportlinkVeldplannerBlok Blok(string? id, string start = "10:00", string veld = "veld 1 A", int duur = 75, string label = "A - B")
        => new(label, veld, start, 0.5m, duur, id);

    /// <summary>Een store in het geheugen die bijhoudt wat er geschreven werd.</summary>
    private sealed class GeheugenStore : IVeldplannerBlokStore
    {
        public Dictionary<string, VeldplannerBlokRij> Rijen { get; } = new(StringComparer.Ordinal);
        public int Schrijfaanroepen { get; private set; }
        public DateOnly? BewaarVanaf { get; private set; }

        public Task<IReadOnlyDictionary<string, BlokOpslagStand>> LeesDagAsync(string clubCode, DateOnly datum, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, BlokOpslagStand>>(Rijen.Values
                .Where(r => r.ClubCode == clubCode && r.Datum == datum)
                .ToDictionary(r => r.PublicMatchId, r => new BlokOpslagStand(r.RijHash, r.ExternalMatchId), StringComparer.Ordinal));

        public Task SchrijfAsync(string clubCode, DateOnly datum, IReadOnlyList<VeldplannerBlokRij> rijen,
            IReadOnlyList<string> verwijderSleutels, DateOnly bewaarVanaf, CancellationToken ct = default)
        {
            Schrijfaanroepen++;
            BewaarVanaf = bewaarVanaf;
            foreach (var rij in rijen) Rijen[rij.PublicMatchId] = rij;
            foreach (var sleutel in verwijderSleutels) Rijen.Remove(sleutel);
            return Task.CompletedTask;
        }
    }

    private static Func<string, Task<long?>> Nummers(Dictionary<string, long?> bekend, List<string>? aanroepen = null)
        => id => { aanroepen?.Add(id); return Task.FromResult(bekend.TryGetValue(id, out var n) ? n : null); };

    private static Task<VeldplannerBlokSynchronisatieResultaat> Sync(
        GeheugenStore store, Func<string, Task<long?>> nummers, params SportlinkVeldplannerBlok[] blokken)
        => VeldplannerBlokSynchronisatie.SynchroniseerAsync(store, nummers, "CLUB", Dag, blokken, Vandaag);

    [Fact]
    public void Hash_IsStabielEnGevoeligVoorElkPlanningsveld()
    {
        var basis = VeldplannerBlokHash.Bereken(Blok("M1"));

        VeldplannerBlokHash.Bereken(Blok("M1")).Should().Be(basis);
        VeldplannerBlokHash.Bereken(Blok("M2")).Should().Be(basis, "de sleutel zelf zit niet in de hash");
        VeldplannerBlokHash.Bereken(Blok("M1", start: "10:05")).Should().NotBe(basis);
        VeldplannerBlokHash.Bereken(Blok("M1", veld: "veld 2 A")).Should().NotBe(basis);
        VeldplannerBlokHash.Bereken(Blok("M1", duur: 80)).Should().NotBe(basis);
        VeldplannerBlokHash.Bereken(Blok("M1", label: "A - C")).Should().NotBe(basis);
        basis.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task EersteKeer_VoegtAlleBlokkenToe_EnLostHetNummerEenmaalOp()
    {
        var store = new GeheugenStore();
        var aanroepen = new List<string>();

        var r = await Sync(store, Nummers(new() { ["M1"] = 101, ["M2"] = 102 }, aanroepen), Blok("M1"), Blok("M2", "11:00"));

        r.Toegevoegd.Should().Be(2);
        r.ExternIdOpgelost.Should().Be(2);
        r.Blokken.Select(b => b.ExternalMatchId).Should().Equal(101, 102);
        store.Rijen["M1"].ExternalMatchId.Should().Be(101);
        aanroepen.Should().BeEquivalentTo("M1", "M2");
    }

    [Fact]
    public async Task TweedeKeerZonderWijziging_SchrijftNietsEnVraagNietsOp()
    {
        var store = new GeheugenStore();
        await Sync(store, Nummers(new() { ["M1"] = 101 }), Blok("M1"));
        var aanroepen = new List<string>();

        var r = await Sync(store, Nummers(new(), aanroepen), Blok("M1"));

        r.Ongewijzigd.Should().Be(1);
        (r.Toegevoegd, r.Gewijzigd, r.Verwijderd).Should().Be((0, 0, 0));
        store.Schrijfaanroepen.Should().Be(1, "alleen de eerste keer is er geschreven");
        aanroepen.Should().BeEmpty("het nummer is onveranderlijk en al bekend");
        r.Blokken.Single().ExternalMatchId.Should().Be(101, "het bewaarde nummer komt terug in de blokken");
    }

    [Fact]
    public async Task EenGewijzigdBlok_SchrijftAlleenDatBlok_EnBehoudtHetNummer()
    {
        var store = new GeheugenStore();
        await Sync(store, Nummers(new() { ["M1"] = 101, ["M2"] = 102 }), Blok("M1"), Blok("M2", "11:00"));

        var r = await Sync(store, Nummers(new()), Blok("M1", "10:30"), Blok("M2", "11:00"));

        (r.Toegevoegd, r.Gewijzigd, r.Verwijderd, r.Ongewijzigd).Should().Be((0, 1, 0, 1));
        store.Rijen["M1"].StartTijd.Should().Be("10:30");
        store.Rijen["M1"].ExternalMatchId.Should().Be(101);
    }

    [Fact]
    public async Task EenBlokDatSportlinkNietMeerToont_WordtVerwijderd()
    {
        var store = new GeheugenStore();
        await Sync(store, Nummers(new() { ["M1"] = 101, ["M2"] = 102 }), Blok("M1"), Blok("M2", "11:00"));

        var r = await Sync(store, Nummers(new()), Blok("M1"));

        r.Verwijderd.Should().Be(1);
        store.Rijen.Keys.Should().Equal("M1");
    }

    [Fact]
    public async Task OnbekendNummer_BlijftNull_EnWordtDeVolgendeKeerOpnieuwOpgevraagd()
    {
        var store = new GeheugenStore();
        var eerste = await Sync(store, Nummers(new()), Blok("M1"));
        eerste.ExternIdOnbekend.Should().Be(1);
        store.Rijen["M1"].ExternalMatchId.Should().BeNull();

        var aanroepen = new List<string>();
        var tweede = await Sync(store, Nummers(new() { ["M1"] = 101 }, aanroepen), Blok("M1"));

        aanroepen.Should().Equal("M1");
        tweede.ExternIdOpgelost.Should().Be(1);
        store.Rijen["M1"].ExternalMatchId.Should().Be(101, "ook als de hash gelijk bleef");
    }

    [Fact]
    public async Task EenFoutBijHetOpvragen_StoptDePlanningNiet()
    {
        var store = new GeheugenStore();
        Func<string, Task<long?>> kapot = id => id == "M1" ? throw new HttpRequestException("storing") : Task.FromResult<long?>(102);

        var r = await Sync(store, kapot, Blok("M1"), Blok("M2", "11:00"));

        r.Toegevoegd.Should().Be(2);
        r.Blokken.Select(b => b.ExternalMatchId).Should().Equal(null, 102L);
        r.ExternIdOnbekend.Should().Be(1);
    }

    [Fact]
    public async Task BlokkenZonderPublicMatchId_WordenNietOpgeslagen_MaarBlijvenInHetResultaat()
    {
        var store = new GeheugenStore();

        var r = await Sync(store, Nummers(new()), Blok(null), Blok("M1"));

        r.ZonderSleutel.Should().Be(1);
        r.Blokken.Should().HaveCount(2);
        store.Rijen.Keys.Should().Equal("M1");
    }

    [Fact]
    public async Task DezelfdeSleutelTweeKeerInEenAntwoord_WordtEenRij_DeLaatsteWint()
    {
        var store = new GeheugenStore();

        await Sync(store, Nummers(new() { ["M1"] = 101 }), Blok("M1", "10:00"), Blok("M1", "10:45"));

        store.Rijen.Should().ContainSingle().Which.Value.StartTijd.Should().Be("10:45");
    }

    [Fact]
    public async Task AndereDagEnAndereClub_WordenNietAangeraakt()
    {
        var store = new GeheugenStore();
        store.Rijen["X"] = new VeldplannerBlokRij("CLUB", "X", Dag.AddDays(7), "A - B", "veld 1", "10:00", 1m, 75, 1, "h");
        store.Rijen["Y"] = new VeldplannerBlokRij("ANDER", "Y", Dag, "A - B", "veld 1", "10:00", 1m, 75, 1, "h");

        await Sync(store, Nummers(new()), Blok("M1"));

        store.Rijen.Keys.Should().BeEquivalentTo("X", "Y", "M1");
    }

    [Fact]
    public async Task Schrijven_GeeftDeBewaardrempelMee()
    {
        var store = new GeheugenStore();

        await Sync(store, Nummers(new()), Blok("M1"));

        store.BewaarVanaf.Should().Be(Vandaag.AddDays(-VeldplannerBlokSynchronisatie.BewaarDagen));
    }

    [Fact]
    public async Task Opvragen_IsBegrensdPerAanvraag()
    {
        var store = new GeheugenStore();
        var blokken = Enumerable.Range(1, VeldplannerBlokSynchronisatie.MaxOpTeLossenPerAanvraag + 20)
            .Select(i => Blok($"M{i}", "10:00", $"veld {i}")).ToArray();
        var aanroepen = new List<string>();

        var r = await Sync(store, Nummers(blokken.ToDictionary(b => b.PublicMatchId!, b => (long?)1), aanroepen), blokken);

        aanroepen.Should().HaveCount(VeldplannerBlokSynchronisatie.MaxOpTeLossenPerAanvraag);
        r.ExternIdOnbekend.Should().Be(20, "de rest volgt bij de volgende aanvraag");
    }

    /// <summary>#1917: eigenaarsbesluit 10 gelijktijdige opvragen (was 6): bewezen met een meting van het werkelijke maximum.</summary>
    [Fact]
    public async Task Opvragen_LopenMet10Tegelijk_NietMeerEnNietMinder()
    {
        VeldplannerBlokSynchronisatie.GelijktijdigeOpvragen.Should().Be(10);
        var store = new GeheugenStore();
        var blokken = Enumerable.Range(1, 40).Select(i => Blok($"M{i}", "10:00", $"veld {i}")).ToArray();
        int actief = 0, maximum = 0;
        Func<string, Task<long?>> opvrager = async _ =>
        {
            var nu = Interlocked.Increment(ref actief);
            int oud;
            while (nu > (oud = Volatile.Read(ref maximum)) && Interlocked.CompareExchange(ref maximum, nu, oud) != oud) { }
            await Task.Delay(40);
            Interlocked.Decrement(ref actief);
            return 1;
        };

        await VeldplannerBlokSynchronisatie.SynchroniseerAsync(store, opvrager, "CLUB", Dag, blokken, Vandaag);

        maximum.Should().Be(10, "precies de grens: genoeg blokken om hem te vullen, nooit erboven");
    }
}
