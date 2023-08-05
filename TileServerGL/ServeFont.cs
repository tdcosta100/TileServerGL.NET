namespace TileServerGL
{
    public class ServeFont
    {
        public static void Init(Configuration configuration, IApplicationBuilder applicationBuilder, IEndpointRouteBuilder endpointRouteBuilder, string routePrefix)
        {
            endpointRouteBuilder.MapGet(@"/{fontstack:regex(^[A-Za-z0-9_\- ]+$)}/{range:regex(^\d+-\d+)}.pbf", (HttpContext context, string fontstack, string range) =>
            {
                var fontFile = Path.Combine(configuration.Options!.Paths!.Fonts, fontstack, $"{range}.pbf");

                if (!File.Exists(fontFile))
                {
                    return Results.NotFound();
                }

                context.Response.ContentType = "application/x-protobuf";
                return Results.File(File.ReadAllBytes(fontFile));
            });
        }
    }
}
