using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Planner.Endpoints.Http;

/// <summary>Aligns the worker HTTP server with the largest existing bounded route envelope.</summary>
public static class FunctionsHttpBodyHost
{
    public static FunctionsApplicationBuilder ConfigureBoundedFunctionsWebApplication(
        this FunctionsApplicationBuilder builder)
    {
        builder.ConfigureFunctionsWebApplication();
        builder.Services.ConfigureBoundedHttpBodies();
        return builder;
    }

    /// <summary>
    /// Finite host ceiling: the existing 5 MiB CSV plus worst-case JSON escaping and filename.
    /// Smaller endpoint limits remain enforced by <see cref="BoundedRequestBodyReader"/>.
    /// </summary>
    public static IServiceCollection ConfigureBoundedHttpBodies(this IServiceCollection services)
        => services.Configure<KestrelServerOptions>(options =>
            options.Limits.MaxRequestBodySize = TeambegeleidingImportRequestBodyLimits.MaxJsonBytes);
}
