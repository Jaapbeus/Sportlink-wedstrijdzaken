namespace Planner.Shared.Autorisatie;

public interface IPlannerAanvragerRequest
{
    string? AangevraagdDoor { get; set; }
}

public interface IBevestigAanvragerRequest : IPlannerAanvragerRequest
{
    string Datum { get; }
    string AanvangsTijd { get; }
}

public interface IHerplanAanvragerRequest : IPlannerAanvragerRequest
{
    long Wedstrijdcode { get; }
    string GewensteAanvangsTijd { get; }
}
