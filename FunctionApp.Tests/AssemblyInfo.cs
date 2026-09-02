using Xunit;

// #867: SportlinkFixtureSyncIntegrationTests populeert SystemUtilities.AppSettings' procesbrede,
// statische instellingencache (via AppSettings.SetForTests) om SportlinkSyncPipeline.RunSyncAsync
// rechtstreeks te kunnen aanroepen. Die cache is gedeeld met elke andere testklasse die
// AppSettings.GetSetting/RequireSetting/RequireClubCode aanroept (bijv.
// EmailTemplateServiceTests.RequireClubCode_GeenClubCode_GooitException, die verwacht dat 'clubCode'
// ontbreekt). Zonder deze guard zou xUnit's standaard cross-class-parallellisme die test kunnen
// laten falen (of stil laten slagen om de verkeerde reden) afhankelijk van de toevallige
// uitvoeringsvolgorde. Serialiseren van alle testklassen is hier goedkoper dan een dependency-
// injectie-herontwerp van een statische cache die al sinds v1 bestaat.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
