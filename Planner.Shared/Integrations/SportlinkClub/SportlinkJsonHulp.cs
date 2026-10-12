using System.Text.Json;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Stateloze JSON-hulpen voor de Sportlink-leesaanroepen: envelope-uitpakken, vormbeschrijving en picklist-parsing (#1631).</summary>
internal static class SportlinkJsonHulp
{
    /// <summary>
    /// Sportlink-responsen zijn soms een kale JSON-array en soms genest onder een envelope-property
    /// (naam en casing per endpoint verschillend, niet 100% live bevestigd) — deze helper zoekt de
    /// array op één van beide manieren zodat elke fetch-methode niet zijn eigen kopie hoeft te houden.
    /// </summary>
    /// <summary>
    /// #1427: als een object precies één array-property heeft, is dat vrijwel zeker de lijst —
    /// ongeacht hoe Sportlink die property noemt. Meerdere arrays → <c>null</c>: niet gokken.
    /// </summary>
    internal static JsonElement? EnigeArrayProperty(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var arrays = root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array).ToList();
        return arrays.Count == 1 ? arrays[0].Value : null;
    }

    /// <summary>
    /// #1427: beschrijft de vorm van een JSON-respons zonder één waarde te tonen — bijv.
    /// <c>Object{Foo:Array[12],Bar:String}</c>. Veilig om te loggen; maximaal tien properties.
    /// </summary>
    internal static string BeschrijfJsonStructuur(JsonElement root) => root.ValueKind switch
    {
        JsonValueKind.Object => "Object{" + string.Join(",", root.EnumerateObject().Take(10).Select(p =>
            p.Value.ValueKind == JsonValueKind.Array ? $"{p.Name}:Array[{p.Value.GetArrayLength()}]" : $"{p.Name}:{p.Value.ValueKind}")) + "}",
        JsonValueKind.Array => $"Array[{root.GetArrayLength()}]",
        _ => root.ValueKind.ToString()
    };

    internal static JsonElement? UnwrapArrayEnvelope(JsonElement root, params string[] propertyNames)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root;

        foreach (var name in propertyNames)
        {
            if (root.TryGetProperty(name, out var property))
                return property;
        }

        return null;
    }

    /// <summary>
    /// ONBEVESTIGD: welke JSON-veldnamen een picklist-item daadwerkelijk gebruikt is nooit met een
    /// netwerktrace gezien — probeert daarom een paar aannemelijke namen per waarde, in volgorde
    /// van waarschijnlijkheid, in plaats van een strikt contract af te dwingen dat mogelijk meteen
    /// breekt op de eerste live respons.
    /// </summary>
    internal static SportlinkPickListItem ParsePickListItem(JsonElement item)
    {
        var id = FirstStringProperty(item, "Id", "PublicTeamId", "PublicLocationId", "FacilityId", "Value", "Code");
        var naam = FirstStringProperty(item, "Name", "TeamName", "NormalizedName", "LocationName", "FacilityName", "Text", "Description", "Naam");
        return new SportlinkPickListItem(id, naam);
    }

    internal static string? FirstStringProperty(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString();
        }

        return null;
    }
}
