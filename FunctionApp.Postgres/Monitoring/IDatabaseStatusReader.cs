using Planner.Shared.Monitoring;

namespace FunctionApp.Postgres.Monitoring;

/// <summary>
/// Stelt vast of de Postgres-database draait (#1268 — port van de SQL Server-tier se #831).
///
/// <para>
/// Het contract wijkt bewust af van de SQL Server-tegenhanger: die krijgt vier Azure-resource-namen
/// mee omdat een ARM-URL die nodig heeft. Een beheerde Postgres-omgeving wordt niet zo geadresseerd,
/// dus die parameters zouden hier lege ceremonie zijn.
/// </para>
///
/// <para>
/// Abstract gehouden om dezelfde reden als op de andere tier: de beslislogica
/// (<see cref="DatabaseUitvalCore"/>) moet testbaar zijn zonder netwerk en zonder database.
/// </para>
/// </summary>
public interface IDatabaseStatusReader
{
    /// <summary>
    /// Haalt de huidige toestand op. Gooit bij een fout die niets over de database zegt (bijv. een
    /// onbereikbare management-API) — de aanroeper logt dat en doet verder niets, zodat een storing
    /// in de controle zelf nooit als databasestoring wordt gemeld.
    /// </summary>
    Task<DatabaseStatusInfo> LeesStatusAsync(CancellationToken annuleringstoken = default);
}
