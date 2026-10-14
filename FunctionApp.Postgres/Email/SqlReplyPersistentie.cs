using Planner.Shared.Email;

namespace FunctionApp.Postgres.Email;

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
