using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

// #891: minimale host-bootstrap voor de Postgres-tier — bewust géén 1-op-1-kopie van
// FunctionApp/Program.cs' DI-registraties (Graph, AI, e-mail, noodmail-throttle, monitoring): die
// horen bij de functionaliteit die #887-#890 vertalen, niet bij de projectopzet zelf. Dit project
// bevat vandaag uitsluitend wat nodig is om te bouwen, op te starten, en /api/health te bewijzen.
var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

builder.Build().Run();
