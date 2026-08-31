using Xunit;

// Zelfde reden als Database.Postgres.Tests/AssemblyInfo.cs: alle integratietests hierin draaien
// tegen dezelfde, gedeelde Postgres-instantie en raken deels dezelfde schema's (PostgresMergeOrchestrator
// doet 'CREATE SCHEMA IF NOT EXISTS stg/his', wat niet atomair is tegen een gelijktijdige identieke
// create — empirisch gevonden bij #854). Voor tests tegen een gedeelde externe resource is
// sequentiële uitvoering de juiste keuze, niet een advisory lock in productiecode.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
