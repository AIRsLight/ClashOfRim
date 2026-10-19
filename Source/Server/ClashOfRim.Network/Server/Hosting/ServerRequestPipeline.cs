using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network.Hosting;

internal static class ServerRequestPipeline
{
    internal static void Use(WebApplication app, SessionPrincipalAuthorization principals, SnapshotMutationCoordinator mutations, ILogger logger)
    {
        app.Use(async (context, next) =>
        {
            if (MultipartSnapshotAuthentication.IsProtectedRoute(context.Request.Path)
                && MultipartSnapshotAuthentication.Authorize(
                    context.Request,
                    principals,
                    DateTimeOffset.UtcNow) is { } rejection)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(rejection);
                return;
            }

            await next(context);
        });
        UseRequestDiagnostics(app, logger);
        app.Use(async (context, next) =>
        {
            // Let endpoints with explicit ownership control validation and recovery order.
            // Keep the fallback for routes that have not yet acquired explicit recovery ownership.
            if (context.GetEndpoint()?.Metadata.GetMetadata<EndpointSnapshotRecovery>() is null)
                mutations.Recover();
            await next(context);
        });
    }

    private static void UseRequestDiagnostics(WebApplication app, ILogger logger)
    {
        app.Use(async (context, next) =>
        {
            string? language = context.Request.Headers[ServerLocalization.LanguageHeader].FirstOrDefault();
            using ServerLocalizationScope scope = ServerLocalization.BeginRequest(language);
            try
            {
                await next(context);
                if (context.Response.StatusCode >= StatusCodes.Status400BadRequest)
                {
                    logger.LogWarning(
                        "HTTP {StatusCode} for {Method} {Path}{QueryString}",
                        context.Response.StatusCode,
                        context.Request.Method,
                        context.Request.Path,
                        context.Request.QueryString);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Unhandled exception while processing {Method} {Path}{QueryString}",
                    context.Request.Method,
                    context.Request.Path,
                    context.Request.QueryString);

                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        result = ProtocolResponse.Reject(
                            ProtocolErrorCode.ServerRejected,
                            ServerLocalization.Text("Server.InternalError"))
                    });
                }
            }
        });
    }
}
