namespace FunctionApp.Postgres.Email;

/// <summary>
/// De smalle opslagnaad van <see cref="EmailReplyPolicyService"/> (#1568, review M6): precies de zes
/// schrijfacties van de reply-afhandeling, zodat de zekerheidspoort zonder database getest kan worden.
/// Bewust geen tier-brede persistence-interface zoals op de SQL Server-tier — die afweging staat in de
/// klassekop van <see cref="EmailReplyPolicyService"/>; dit is alleen de testnaad, de productie-implementatie
/// roept de bestaande statische repository aan.
/// </summary>
internal interface IReplyPersistentie
{
    Task UpdateVoorgesteldAntwoordAsync(int verwerkingId, string antwoordEmail);
    Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? geextraheerdeData);
    Task UpdateFoutAsync(int verwerkingId, string foutMelding);
    Task MarkeerVerzendPogingAsync(int verwerkingId);
    Task WisVerzendPogingAsync(int verwerkingId);
    Task UpdateAntwoordVerstuurdAsync(int verwerkingId, string verstuurdNaar, string antwoordEmail);
}

/// <summary>Productie-implementatie: delegeert naar <see cref="SqlEmailPersistenceRepository"/> met de connectiestring.</summary>
internal sealed class SqlReplyPersistentie(string connectionString) : IReplyPersistentie
{
    public Task UpdateVoorgesteldAntwoordAsync(int verwerkingId, string antwoordEmail)
        => SqlEmailPersistenceRepository.UpdateVoorgesteldAntwoordAsync(connectionString, verwerkingId, antwoordEmail);

    public Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? geextraheerdeData)
        => SqlEmailPersistenceRepository.UpdateStatusAsync(connectionString, verwerkingId, status, geextraheerdeData);

    public Task UpdateFoutAsync(int verwerkingId, string foutMelding)
        => SqlEmailPersistenceRepository.UpdateFoutAsync(connectionString, verwerkingId, foutMelding);

    public Task MarkeerVerzendPogingAsync(int verwerkingId)
        => SqlEmailPersistenceRepository.MarkeerVerzendPogingAsync(connectionString, verwerkingId);

    public Task WisVerzendPogingAsync(int verwerkingId)
        => SqlEmailPersistenceRepository.WisVerzendPogingAsync(connectionString, verwerkingId);

    public Task UpdateAntwoordVerstuurdAsync(int verwerkingId, string verstuurdNaar, string antwoordEmail)
        => SqlEmailPersistenceRepository.UpdateAntwoordVerstuurdAsync(connectionString, verwerkingId, verstuurdNaar, antwoordEmail);
}
