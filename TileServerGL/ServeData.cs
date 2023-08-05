using Mapbox.Vector.Tile;
using MaplibreNative;
using System.IO.Compression;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TileServerGL
{
    public class ServeData
    {
        private class LowerCaseNamingPolicy : JsonNamingPolicy
        {
            public override string ConvertName(string name) => name.ToLower();
        }

        public static void Init(Configuration configuration, IApplicationBuilder applicationBuilder, IEndpointRouteBuilder endpointRouteBuilder, string routePrefix)
        {
            var fileSource = FileSourceManager.GetFileSource(FileSourceType.Mbtiles, ResourceOptions.Default());
            var lowerCaseNamingPolicy = new LowerCaseNamingPolicy();
            var serveBounds = new[]
            {
                Math.Min(configuration.Options!.ServeBounds[0], configuration.Options.ServeBounds[2]),
                Math.Min(configuration.Options.ServeBounds[1], configuration.Options.ServeBounds[3]),
                Math.Max(configuration.Options.ServeBounds[0], configuration.Options.ServeBounds[2]),
                Math.Max(configuration.Options.ServeBounds[1], configuration.Options.ServeBounds[3])
            };

            endpointRouteBuilder.MapGet(@"/{id:regex(^[A-Za-z0-9_\-]+$)}/{z:int:min(0)}/{x:int:min(0)}/{y:int:min(0)}.{format:regex(^\w+$)}", (HttpContext context, string id, int x, int y, byte z, string format) =>
            {
                if (!configuration.Data.ContainsKey(id))
                {
                    return Results.NotFound();
                }

                var data = configuration.Data[id];
                var tileJSONFormat = data.TileJSON!["format"]!.GetValue<string>();

                if (
                    format != tileJSONFormat
                    &&
                    !(format == "geojson" && tileJSONFormat == "pbf")
                )
                {
                    return Results.BadRequest("Invalid format");
                }

                if (
                    z < (data.TileJSON["minzoom"]?.GetValue<int>() ?? 0)
                    || z > (data.TileJSON["maxzoom"]?.GetValue<int>() ?? 22)
                    || x < Util.LongitudeToTileX(serveBounds[0], z)
                    || x > Util.LongitudeToTileX(serveBounds[2], z)
                    || y < Util.LatitudeToTileY(serveBounds[3], z)
                    || y > Util.LatitudeToTileY(serveBounds[1], z)
                )
                {
                    return Results.BadRequest("Out of bounds");
                }

                Response? response = null;

                using (var runLoop = new RunLoop())
                {

                    var request = fileSource.Request(Resource.Tile(string.Format("mbtiles://{0}?tile={{z}}/{{x}}/{{y}}.{1}", Path.Combine(configuration.Options!.Paths!.MBTiles, data.MBTiles), tileJSONFormat), 1.0f, x, y, z, Resource.TilesetScheme.XYZ), res =>
                    {
                        response = res;
                        runLoop.Stop();
                    });

                    runLoop.Run();
                }

                if (response!.Error != null)
                {
                    return Results.Problem(response.Error.Message);
                }

                if (response.NoContent)
                {
                    return Results.NoContent();
                }

                var responseData = response.Data;

                if (tileJSONFormat == "pbf" && format == "geojson")
                {
                    if (responseData!.Take(Util.GZipSignature.Length).SequenceEqual(Util.GZipSignature))
                    {
                        using var memoryStreamIn = new MemoryStream(responseData!);
                        using var memoryStreamOut = new MemoryStream();
                        using var gzipStream = new GZipStream(memoryStreamOut, CompressionMode.Decompress);

                        memoryStreamIn.CopyTo(gzipStream);
                        gzipStream.Flush();
                        responseData = memoryStreamOut.ToArray();
                    }

                    using var memoryStream = new MemoryStream(responseData!);
                    var layers = VectorTileParser.Parse(memoryStream);

                    var geoJSON = new JsonObject()
                    {
                        ["type"] = "FeatureCollection",
                    };

                    var features = new List<JsonNode?>();

                    foreach (var layer in layers)
                    {
                        features.AddRange(
                            layer.ToGeoJSON(x, y, z).Features
                            .Select(feature =>
                            {
                                feature.Properties.Add("layer", layer.Name);

                                return JsonSerializer.SerializeToNode(feature, new JsonSerializerOptions() { PropertyNamingPolicy = lowerCaseNamingPolicy });
                            })
                        );
                    }

                    geoJSON["features"] = new JsonArray(features.ToArray());

                    responseData = Encoding.UTF8.GetBytes(geoJSON.ToJsonString());
                }

                if ((format == "pbf" || format == "geojson") && !responseData!.Take(Util.GZipSignature.Length).SequenceEqual(Util.GZipSignature))
                {
                    using (var memoryStreamIn = new MemoryStream(responseData!))
                    using (var memoryStreamOut = new MemoryStream())
                    using (var gzipStream = new GZipStream(memoryStreamOut, CompressionMode.Compress))
                    {
                        memoryStreamIn.CopyTo(gzipStream);
                        gzipStream.Flush();
                        responseData = memoryStreamOut.ToArray();
                    }

                    context.Response.Headers.ContentEncoding = "gzip";
                }

                string? contentType = null;

                switch (format)
                {
                    case "png":
                        contentType = "image/png";
                        break;
                    case "jpg":
                        contentType = MediaTypeNames.Image.Jpeg;
                        break;
                    case "geojson":
                        contentType = MediaTypeNames.Application.Json;
                        break;
                    case "pbf":
                        contentType = "application/x-protobuf";
                        break;
                    default:
                        break;
                }

                return Results.File(responseData!, contentType);
            });

            endpointRouteBuilder.MapGet(@"/{id:regex(^[A-Za-z0-9_\-]+$)}.json", (HttpContext context, string id) =>
            {
                if (!configuration.Data.ContainsKey(id))
                {
                    return Results.NotFound();
                }

                var tileJSON = configuration.Data[id].TileJSON.Deserialize<JsonNode>();

                tileJSON!["tiles"] = new JsonArray(new[] { JsonValue.Create(string.Format("{0}data/{1}/{{z}}/{{x}}/{{y}}.{2}", Util.GetPublicUrl(context.Request), id, tileJSON["format"])) });

                return Results.Json(tileJSON);
            });
        }
    }
}
