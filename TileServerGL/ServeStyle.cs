using Microsoft.AspNetCore.Rewrite;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TileServerGL
{
    public class ServeStyle
    {
        public static void Init(Configuration configuration, ILogger logger, IHostApplicationLifetime lifetime, IApplicationBuilder applicationBuilder, IEndpointRouteBuilder endpointRouteBuilder, string routePrefix)
        {
            endpointRouteBuilder.MapGet(@"/{id:regex(^[A-Za-z0-9_\-]+$)}/style.json", (HttpContext context, string? id) =>
            {
                if (id == null || !configuration.Styles.ContainsKey(id))
                {
                    return Results.NotFound();
                }

                var styleJSON = configuration.Styles[id].StyleJSON.Deserialize<JsonNode>();

                if (styleJSON!["sources"] != null)
                {
                    foreach (var source in styleJSON["sources"]!.AsObject())
                    {
                        if (source.Value!["url"]?.GetValue<string>()?.StartsWith("local://") ?? false)
                        {
                            source.Value!["url"] = source.Value["url"]!.GetValue<string>().Replace("local://", Util.GetPublicUrl(context.Request));
                        }
                    }
                }

                if (styleJSON["sprite"]?.GetValue<string>()?.StartsWith("local://") ?? false)
                {
                    styleJSON["sprite"] = styleJSON["sprite"]!.GetValue<string>().Replace("local://", Util.GetPublicUrl(context.Request));
                }

                if (styleJSON["glyphs"]?.GetValue<string>()?.StartsWith("local://") ?? false)
                {
                    styleJSON["glyphs"] = styleJSON["glyphs"]!.GetValue<string>().Replace("local://", Util.GetPublicUrl(context.Request));
                }

                return Results.Json(styleJSON);
            });

            applicationBuilder.UseRewriter(new RewriteOptions().AddRewrite(
                regex: $@"^{routePrefix.Substring(1)}/(?<id>[^/]+)/sprite\.(?<format>.+)$",
                replacement: $"{routePrefix}/${{id}}/sprite@1x.${{format}}",
                skipRemainingRules: true
            ));

            endpointRouteBuilder.MapGet(@"/{id:regex(^[A-Za-z0-9_\-]+$)}/sprite@{scale:int:min(1)}x.{format:regex(^\w+$)}", (HttpContext context, string id, int scale, string format) =>
            {
                if (!configuration.Styles.ContainsKey(id) || string.IsNullOrWhiteSpace(configuration.Styles[id].SpritePath))
                {
                    return Results.NotFound();
                }

                var spriteFile = $"{Path.Combine(configuration.Options!.Paths!.Sprites, configuration.Styles[id].SpritePath)}{(scale > 1 ? $"@{scale}x" : string.Empty)}.{format}";

                string? contentType = null;

                try
                {
                    switch (format)
                    {
                        case "json":
                            contentType = MediaTypeNames.Application.Json;
                        break;
                        case "png":
                            contentType = "image/png";
                        break;
                        default:
                            break;
                    }

                    return Results.File(File.ReadAllBytes(spriteFile), contentType);
                }
                catch (Exception ex)
                {
                    return Results.Problem($"Sprite load error: {spriteFile}");
                }
            });
        }
    }
}
