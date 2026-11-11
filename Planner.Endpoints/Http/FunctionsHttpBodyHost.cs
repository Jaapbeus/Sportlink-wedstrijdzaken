using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Planner.Endpoints.Http;

/// <summary>Keeps a finite transport ceiling separate from the stricter application body limits.</summary>
public static class FunctionsHttpBodyHost
{
    // Azure Functions' documented maximum request size. Do not disable the worker limit:
    // Kestrel needs room to drain rejected bodies (and HTTP/1 chunk framing) so the
    // Functions/YARP bridge can deliver the application response without replaying a stream.
    public const long MaxTransportBodyBytes = 220_200_960;

    public static FunctionsApplicationBuilder ConfigureBoundedFunctionsWebApplication(
        this FunctionsApplicationBuilder builder)
    {
        builder.ConfigureFunctionsWebApplication();
        builder.Services.ConfigureBoundedHttpBodies();
        return builder;
    }

    /// <summary>
    /// Finite transport ceiling matching the Functions platform maximum; no platform setting is raised.
    /// All stricter endpoint limits remain enforced by <see cref="BoundedRequestBodyReader"/>.
    /// </summary>
    public static IServiceCollection ConfigureBoundedHttpBodies(this IServiceCollection services)
        => services.Configure<KestrelServerOptions>(options =>
            options.Limits.MaxRequestBodySize = MaxTransportBodyBytes);
}
