using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

// Application Insights wordt automatisch geconfigureerd via host.json
// en de APPINSIGHTS_INSTRUMENTATIONKEY app setting in Azure

builder.Build().Run();