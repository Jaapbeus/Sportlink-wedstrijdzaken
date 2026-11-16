using BlazorAdmin.Models;

namespace BlazorAdmin.Pages;

public partial class Instellingen
{
    private void OnthoudEmailInstellingen(AppSettingsDto huidig)
    {
        _opgeslagenEmailReviewModus = huidig.EmailReviewModusActief;
        _opgeslagenEmailVerwerking = huidig.EmailVerwerkingIngeschakeld;
        _opgeslagenEmailReviewOntvanger = huidig.EmailReviewOntvanger;
    }

    private void NaOpslaanEmailInstellingen(AppSettingsDto huidig)
    {
        huidig.EmailReviewOntvanger = string.IsNullOrWhiteSpace(huidig.EmailReviewOntvanger)
            ? null : huidig.EmailReviewOntvanger.Trim();
        OnthoudEmailInstellingen(huidig);
        _emailReviewBevestigd = false;
        _toonEmailReviewBevestiging = false;
    }

    private async Task BevestigEmailReviewUitzettenAsync()
    {
        _toonEmailReviewBevestiging = false;
        _emailReviewBevestigd = true;
        await OpslaanAsync();
    }

    private void AnnuleerEmailReviewUitzetten()
    {
        _toonEmailReviewBevestiging = false;
        _emailReviewBevestigd = false;
        if (settings != null) settings.EmailReviewModusActief = true;
    }

}
