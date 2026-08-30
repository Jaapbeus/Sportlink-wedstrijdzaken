using Xunit;

// Alle integratietests in dit project draaien tegen dezelfde, gedeelde live Postgres-instantie
// (zie PostgresMergeOrchestratorIntegrationTests voor de wegwerpcontainer-instructies). xUnit
// draait testklassen standaard parallel over meerdere threads — empirisch gevonden (#854-verificatie):
// dat liet meerdere testklassen tegelijk 'CREATE SCHEMA IF NOT EXISTS stg/his' uitvoeren (via
// PostgresMergeOrchestrator.EnsureSchemaAsync) en gaf dezelfde race als eerder gevonden in
// MigrationRunner (#821): Postgres' IF NOT EXISTS is niet atomair tegen een gelijktijdige
// identieke create. Voor tests tegen een gedeelde externe resource is sequentiële uitvoering de
// juiste keuze, niet een advisory lock in productiecode — de race bestaat alleen omdat losse
// testklassen elkaar niet coördineren.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
