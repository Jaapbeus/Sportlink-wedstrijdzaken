namespace BlazorAdmin.Services;

/// <summary>
/// DelegatingHandler die automatisch de X-Club-Code header toevoegt aan elk API-request.
/// Geregistreerd als InnerHandler van de AdminApiClient's HttpClient in Program.cs.
/// Alle 37+ methoden in AdminApiClient profiteren automatisch van deze header.
/// </summary>
public class ClubCodeHeaderHandler : DelegatingHandler
{
    private readonly ClubSelectorService _clubSelector;

    public ClubCodeHeaderHandler(ClubSelectorService clubSelector)
    {
        _clubSelector = clubSelector;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var code = _clubSelector.SelectedClubCode;
        if (!string.IsNullOrWhiteSpace(code))
            request.Headers.TryAddWithoutValidation("X-Club-Code", code);

        return base.SendAsync(request, cancellationToken);
    }
}
