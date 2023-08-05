using HandlebarsDotNet;
using MaplibreNative;
using Microsoft.Extensions.Primitives;
using System.Net.Mime;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting.Server;

namespace TileServerGL
{
    public class Server
    {
        public static void Init(Configuration configuration, IApplicationBuilder applicationBuilder, IEndpointRouteBuilder endpointRouteBuilder, IWebHostEnvironment webHostEnvironment)
        {
            var handlebars = Handlebars.Create();

            configuration.Options!.Paths!.Styles = Util.ResolvePath(configuration.Options.Paths.Root, configuration.Options.Paths.Styles ?? string.Empty);
            configuration.Options.Paths.Fonts = Util.ResolvePath(configuration.Options.Paths.Root, configuration.Options.Paths.Fonts ?? string.Empty);
            configuration.Options.Paths.Sprites = Util.ResolvePath(configuration.Options.Paths.Root, configuration.Options.Paths.Sprites ?? string.Empty);
            configuration.Options.Paths.MBTiles = Util.ResolvePath(configuration.Options.Paths.Root, configuration.Options.Paths.MBTiles ?? string.Empty);
            configuration.Options.Paths.Icons = Util.ResolvePath(configuration.Options.Paths.Root, configuration.Options.Paths.Icons ?? string.Empty);

            var checkPath = (string path, string description) =>
            {
                if (!Directory.Exists(path))
                {
                    Environment.FailFast($"The specified path for \"{description}\" does not exist ({path}).");
                }
            };

            checkPath(configuration.Options.Paths.Styles!, "styles");
            checkPath(configuration.Options.Paths.Fonts!, "fonts");
            checkPath(configuration.Options.Paths.Sprites!, "sprites");
            checkPath(configuration.Options.Paths.MBTiles!, "mbtiles");
            checkPath(configuration.Options.Paths.Icons!, "icons");

            var sourceFromDataRegex = new Regex(@"^mbtiles://\{(?<source>[^\}]+)\}$");

            foreach (var styleEntry in configuration.Styles.ToArray())
            {
                try
                {
                    styleEntry.Value.StyleJSON = JsonNode.Parse(
                        !Util.HttpRegex.IsMatch(styleEntry.Value.Style)
                        ?
                        File.ReadAllText(Path.IsPathFullyQualified(styleEntry.Value.Style) ? styleEntry.Value.Style : Path.Combine(configuration.Options.Paths.Styles!, styleEntry.Value.Style))
                        :
                        new HttpClient().GetStringAsync(styleEntry.Value.Style).Result
                    );

                    var tileJSON = new JsonObject()
                    {
                        ["tilejson"] = "2.0.0",
                        ["name"] = styleEntry.Value.StyleJSON!["name"].Deserialize<JsonNode>(),
                        ["attribution"] = "",
                        ["minzoom"] = 0,
                        ["maxzoom"] = 20,
                        ["bounds"] = new JsonArray(-180, -85.0511, 180, 85.0511),
                        ["format"] = "png",
                        ["type"] = "baselayer"
                    };

                    if (styleEntry.Value.TileJSON == null)
                    {
                        styleEntry.Value.TileJSON = new JsonObject();
                    }

                    foreach (var property in tileJSON)
                    {
                        if (styleEntry.Value.TileJSON[property.Key] == null)
                        {
                            styleEntry.Value.TileJSON[property.Key] = property.Value.Deserialize<JsonNode>();
                        }
                    }

                    if (styleEntry.Value.StyleJSON["center"] != null && styleEntry.Value.StyleJSON["zoom"] != null)
                    {
                        styleEntry.Value.TileJSON["center"] = new JsonArray(
                            styleEntry.Value.StyleJSON["center"]![0]!.GetValue<double>(),
                            styleEntry.Value.StyleJSON["center"]![1]!.GetValue<double>(),
                            styleEntry.Value.StyleJSON["zoom"]!.GetValue<double>()
                        );
                    }

                    Util.FixTileJSONCenter(styleEntry.Value.TileJSON);

                    if (styleEntry.Value.StyleJSON["sources"] != null)
                    {
                        foreach (var source in styleEntry.Value.StyleJSON["sources"]!.AsObject())
                        {
                            if (source.Value!["url"] != null)
                            {
                                var url = source.Value["url"]!.GetValue<string>();

                                var match = sourceFromDataRegex.Match(url);

                                if (match.Success)
                                {
                                    source.Value["url"] = match.Result("local://data/${source}.json");
                                }
                            }
                        }
                    }

                    if (styleEntry.Value.StyleJSON["sprite"] != null && !Util.HttpRegex.IsMatch(styleEntry.Value.StyleJSON["sprite"]!.GetValue<string>()))
                    {
                        styleEntry.Value.SpritePath = styleEntry.Value.StyleJSON["sprite"]!.GetValue<string>()
                            .Replace("{style}", Path.GetFileNameWithoutExtension(styleEntry.Value.Style))
                            .Replace("{styleJsonFolder}", Path.GetRelativePath(configuration.Options.Paths.Sprites, Path.GetDirectoryName(Path.Combine(configuration.Options.Paths.Styles!, styleEntry.Value.Style))!));

                        styleEntry.Value.StyleJSON["sprite"] = $"local://styles/{styleEntry.Key}/sprite";
                    }

                    if (styleEntry.Value.StyleJSON["glyphs"] != null && !Util.HttpRegex.IsMatch(styleEntry.Value.StyleJSON["glyphs"]!.GetValue<string>()))
                    {
                        styleEntry.Value.StyleJSON["glyphs"] = "local://fonts/{fontstack}/{range}.pbf";
                    }
                }
                catch (Exception ex)
                {
                    configuration.Styles.Remove(styleEntry.Key);
                }
            }

            using (var runLoop = new RunLoop())
            {
                var fileSource = FileSourceManager.GetFileSource(FileSourceType.Mbtiles, ResourceOptions.Default());
                var removeCenterRegex = new Regex(@",\s*""center""\s*:\s*""[^""]+""");

                foreach (var dataEntry in configuration.Data.ToArray())
                {
                    Response? response = null;
                    
                    fileSource.Request(Resource.Source($"mbtiles://{Path.Combine(configuration.Options.Paths.MBTiles, dataEntry.Value.MBTiles)}"), res =>
                    {
                        response = res;
                        runLoop.Stop();
                    });

                    runLoop.Run();

                    try
                    {
                        var tileJSON = JsonNode.Parse(removeCenterRegex.Replace(System.Text.Encoding.UTF8.GetString(response!.Data), string.Empty));

                        dataEntry.Value.TileJSON = new JsonObject()
                        {
                            ["name"] = dataEntry.Key,
                            ["format"] = "pbf"
                        };

                        foreach (var property in tileJSON!.AsObject())
                        {
                            dataEntry.Value.TileJSON[property.Key] = property.Value.Deserialize<JsonNode>();
                        }

                        tileJSON["tilejson"] = "2.0.0";
                        tileJSON["filesize"] = new FileInfo(Path.Combine(configuration.Options.Paths.MBTiles, dataEntry.Value.MBTiles)).Length;

                        Util.FixTileJSONCenter(dataEntry.Value.TileJSON);
                    }
                    catch (Exception ex)
                    {
                        configuration.Data.Remove(dataEntry.Key);
                    }
                }
            }

            var serveTemplate = (string urlPath, string templateName, Func<HttpRequest, Dictionary<string, object>?> dataGetter) =>
            {
                var templatePath = Path.Combine(webHostEnvironment.ContentRootPath, $@"public\templates\{templateName}.tmpl");

                if (templateName == "index")
                {
                    if (configuration.Options.FrontPage is bool frontPage && !frontPage)
                    {
                        return;
                    }
                    else if (configuration.Options.FrontPage is string frontPageTemplate)
                    {
                        templatePath = Path.GetFullPath(frontPageTemplate, configuration.Options.Paths.Root);
                    }
                }

                var template = handlebars.Compile(File.ReadAllText(templatePath));

                endpointRouteBuilder.MapGet(urlPath, (HttpContext context) =>
                {
                    var templateData = new Dictionary<string, object>();

                    if (dataGetter != null)
                    {
                        templateData = dataGetter(context.Request);

                        if (templateData == null)
                        {
                            return Results.NotFound();
                        }
                    }

                    templateData!.Add("server_version", "TileServerGL.NET v0.1");
                    templateData.Add("public_url", Util.GetPublicUrl(context.Request));
                    templateData.Add("is_light", false);
                    templateData.Add("key_query_part", context.Request.Query["key"] != StringValues.Empty ? $"key={Uri.EscapeDataString(context.Request.Query["key"]!)}&amp;" : string.Empty);
                    templateData.Add("key_query", context.Request.Query["key"] != StringValues.Empty ? $"?key={Uri.EscapeDataString(context.Request.Query["key"]!)}" : string.Empty);
                    templateData.Add("tileSize", Util.TileSize);

                    var contentType = MediaTypeNames.Text.Html;

                    if (templateName == "wmts")
                    {
                        contentType = MediaTypeNames.Text.Xml;
                    }
                    else if (templateName.EndsWith(".json"))
                    {
                        contentType = MediaTypeNames.Application.Json;
                    }

                    return Results.Text(template(templateData), contentType);
                });
            };

            ServeStyle.Init(configuration, applicationBuilder, endpointRouteBuilder.MapGroup("/styles"), "/styles");
            ServeRendered.Init(configuration, applicationBuilder, endpointRouteBuilder.MapGroup("/styles"), "/styles");
            ServeData.Init(configuration, applicationBuilder, endpointRouteBuilder.MapGroup("/data"), "/data");
            ServeFont.Init(configuration, applicationBuilder, endpointRouteBuilder.MapGroup("/fonts"), "/fonts");

            serveTemplate("/", "index", request =>
            {
                var templateData = new Dictionary<string, object>();

                if (configuration.Styles.Any())
                {
                    templateData.Add(
                        "styles",
                        configuration.Styles.ToDictionary(
                            style => style.Key,
                            style =>
                            {
                                var styleData = new Dictionary<string, object>();

                                if (style.Value.StyleJSON!["name"] != null)
                                {
                                    styleData.Add("name", style.Value.StyleJSON["name"]!.GetValue<string>());
                                }

                                styleData.Add("serving_data", style.Value.ServeData);
                                styleData.Add("serving_rendered", style.Value.ServeRendered);

                                if (style.Value.TileJSON!["center"] != null)
                                {
                                    var center = style.Value.TileJSON["center"]!.AsArray();

                                    styleData.Add("viewer_hash", FormattableString.Invariant($"#{Math.Round(center[2]!.GetValue<double>())}/{center[1]!.GetValue<double>():#0.00000}/{center[0]!.GetValue<double>():#0.00000}"));

                                    styleData.Add(
                                        "thumbnail",
                                        string.Format(
                                            "{0}/{1}/{2}.png",
                                            (int)Math.Floor(center[2]!.GetValue<double>()),
                                            Util.LongitudeToTileX(center[0]!.GetValue<double>(), (int)Math.Floor(center[2]!.GetValue<double>())),
                                            Util.LatitudeToTileY(center[1]!.GetValue<double>(), (int)Math.Floor(center[2]!.GetValue<double>()))
                                        )
                                    );
                                }

                                styleData.Add(
                                    "xyz_link",
                                    string.Format(
                                        "{0}styles/{1}/{{z}}/{{x}}/{{y}}.{2}",
                                        Util.GetPublicUrl(request),
                                        style.Key,
                                        style.Value.TileJSON["format"]
                                    )
                                );

                                return styleData;
                            }
                        )
                    );
                }

                if (configuration.Data.Any())
                {
                    templateData.Add(
                        "data",
                        configuration.Data.ToDictionary(
                            data => data.Key,
                            data =>
                            {
                                var dataData = new Dictionary<string, object>();

                                JsonNode? center = null;

                                if (data.Value.TileJSON!["center"] != null)
                                {
                                    center = data.Value.TileJSON["center"]!.AsArray();
                                    dataData.Add("viewer_hash", FormattableString.Invariant($"#{center[2]!.GetValue<double>()}/{center[1]!.GetValue<double>():#0.00000}/{center[0]!.GetValue<double>():#0.00000}"));
                                }

                                dataData.Add("is_vector", data.Value.TileJSON["format"]!.GetValue<string>() == "pbf");

                                if (!(bool)dataData["is_vector"])
                                {
                                    if (center != null)
                                    {
                                        dataData.Add(
                                            "thumbnail",
                                            string.Format(
                                                "{0}/{1}/{2}.{3}",
                                                (int)Math.Floor(center[2]!.GetValue<double>()),
                                                Util.LongitudeToTileX(center[0]!.GetValue<double>(), (int)Math.Floor(center[2]!.GetValue<double>())),
                                                Util.LatitudeToTileY(center[1]!.GetValue<double>(), (int)Math.Floor(center[2]!.GetValue<double>())),
                                                data.Value.TileJSON["format"]
                                            )
                                        );
                                    }

                                    dataData.Add(
                                        "xyz_link",
                                        string.Format(
                                            "{0}data/{1}/{{z}}/{{x}}/{{y}}.{2}",
                                            Util.GetPublicUrl(request),
                                            data.Key,
                                            data.Value.TileJSON["format"]
                                        )
                                    );

                                    if (data.Value.TileJSON["filesize"] != null)
                                    {
                                        var size = data.Value.TileJSON["filesize"]!.GetValue<long>() / 1024.0;
                                        var suffix = "kB";

                                        if (size > 1024.0)
                                        {
                                            suffix = "MB";
                                            size /= 1024.0;
                                        }

                                        if (size > 1024.0)
                                        {
                                            suffix = "GB";
                                            size /= 1024.0;
                                        }

                                        dataData["formatted_filesize"] = $"{size:#0.00} {suffix}";
                                    }
                                }

                                return dataData;
                            }
                        )
                    );
                }

                return templateData;
            });

            serveTemplate(@"/styles/{id:regex(^[A-Za-z0-9_\-]+$)}/", "viewer", request =>
            {
                var id = request.RouteValues["id"] as string;

                if (string.IsNullOrWhiteSpace(id) || !configuration.Styles.ContainsKey(id))
                {
                    return null;
                }

                var style = configuration.Styles[id];

                var templateData = new Dictionary<string, object>
                {
                    { "id", id },
                    { "name", style.StyleJSON!["name"]!.GetValue<string>() },
                    { "serving_data", style.ServeData },
                    { "serving_rendered", style.ServeRendered }
                };
                return templateData;
            });

            serveTemplate(@"/styles/{id:regex(^[A-Za-z0-9_\-]+$)}/wmts.xml", "wmts", request =>
            {
                var id = request.RouteValues["id"] as string;

                if (string.IsNullOrWhiteSpace(id) || !configuration.Styles.ContainsKey(id))
                {
                    return null;
                }

                var style = configuration.Styles[id];

                if (!style.ServeRendered)
                {
                    return null;
                }

                var templateData = new Dictionary<string, object>
                {
                    { "id", id },
                    { "name", style.StyleJSON!["name"]!.GetValue<string>() },
                    { "baseUrl", Util.GetPublicUrl(request) }
                };
                return templateData;
            });

            serveTemplate(@"/data/{id:regex(^[A-Za-z0-9_\-]+$)}/", "data", request =>
            {
                var id = request.RouteValues["id"] as string;

                if (string.IsNullOrWhiteSpace(id) || !configuration.Data.ContainsKey(id))
                {
                    return null;
                }

                var data = configuration.Data[id];

                var templateData = new Dictionary<string, object>
                {
                    { "id", id },
                    { "name", data.TileJSON!["name"]!.GetValue<string>() },
                    { "is_vector", data.TileJSON!["format"]!.ToString() == "pbf" }
                };

                return templateData;
            });

        }
    }
}
