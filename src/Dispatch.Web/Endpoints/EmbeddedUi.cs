using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace Dispatch.Web.Endpoints;

/// <summary>Serves the embedded React SPA on the web port, with SPA fallback to index.html (spec §19.4).</summary>
public static class EmbeddedUi
{
    private static readonly IFileProvider Provider =
        new ManifestEmbeddedFileProvider(typeof(EmbeddedUi).Assembly, "wwwroot");

    /// <summary>Static-file middleware for the SPA, scoped to the web port. Call before mapping endpoints.</summary>
    public static void UseEmbeddedUi(this WebApplication app, int webPort)
    {
        app.UseWhen(ctx => ctx.Connection.LocalPort == webPort, branch =>
        {
            branch.UseDefaultFiles(new DefaultFilesOptions { FileProvider = Provider });
            branch.UseStaticFiles(new StaticFileOptions { FileProvider = Provider });
        });
    }

    /// <summary>SPA fallback so client-side routes (e.g. /messages) return index.html. Call after endpoints.</summary>
    public static void MapEmbeddedUiFallback(this WebApplication app, int webPort)
    {
        app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = Provider })
            .AddEndpointFilter(async (ctx, next) =>
                ctx.HttpContext.Connection.LocalPort == webPort ? await next(ctx) : Results.NotFound());
    }
}
