namespace BlazorAdmin.Pages;

internal static class DagNamen
{
    public static string Naam(int dag) => dag switch
    {
        1 => "Maandag",
        2 => "Dinsdag",
        3 => "Woensdag",
        4 => "Donderdag",
        5 => "Vrijdag",
        6 => "Zaterdag",
        7 => "Zondag",
        _ => $"Dag {dag}"
    };
}
