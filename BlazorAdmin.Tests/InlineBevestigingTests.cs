using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

public sealed class InlineBevestigingTests
{
    [Fact]
    public void Bevestig_VereistEerstVraagEnDezelfdeDoelactie()
    {
        var bevestiging = new InlineBevestiging<(string Type, int Id)>();

        Assert.False(bevestiging.Bevestig(("veld", 1)));
        bevestiging.Vraag(("veld", 1));
        Assert.False(bevestiging.Bevestig(("veld", 2)));
        Assert.True(bevestiging.Bevestig(("veld", 1)));
        Assert.False(bevestiging.IsOpen);
    }

    [Fact]
    public void Annuleer_SluitBevestigingZonderActie()
    {
        var bevestiging = new InlineBevestiging<string>();
        bevestiging.Vraag("delete");

        bevestiging.Annuleer();

        Assert.False(bevestiging.IsOpen);
        Assert.False(bevestiging.Bevestig("delete"));
    }
}
