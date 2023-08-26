using DotSpatial.Projections;
using MaplibreNative;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.Extensions.Primitives;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TileServerGL
{
    public class Renderer : IDisposable
    {
        private Thread _RendererThread;
        private bool _Disposed = false;
        private RunLoop? _RunLoop = null;
        private HeadlessFrontend? _Frontend = null;
        private Map? _Map = null;
        private BlockingCollection<Action<(RunLoop RunLoop, HeadlessFrontend FrontEnd, Map Map)>> _RenderQueue = new();
        private CancellationTokenSource _CancellationTokenSource = new();

        public Renderer(Func<(RunLoop RunLoop, HeadlessFrontend FrontEnd, Map Map)> initializer)
        {
            _RendererThread = new Thread(() =>
            {
                Thread.CurrentThread.Name = $"Renderer {Environment.CurrentManagedThreadId}";

                (_RunLoop, _Frontend, _Map) = initializer();

                try
                {
                    while (_RenderQueue.TryTake(out Action<(RunLoop RunLoop, HeadlessFrontend FrontEnd, Map Map)>? action, Timeout.Infinite, _CancellationTokenSource.Token))
                    {
                        try
                        {
                            action((_RunLoop, _Frontend, _Map));
                        }
                        finally
                        {
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }

                _Map.Dispose();
                _Frontend.Dispose();
                _RunLoop.Dispose();
            });

            if (OperatingSystem.IsWindows())
            {
                _RendererThread.SetApartmentState(ApartmentState.STA);
            }

            _RendererThread.Start();
        }

        public void AddToRenderQueue(Action<(RunLoop RunLoop, HeadlessFrontend FrontEnd, Map Map)> action)
        {
            _RenderQueue.Add(action);
        }

        public Task InvokeAsync(Action<(RunLoop RunLoop, HeadlessFrontend FrontEnd, Map Map)> action)
        {
            var taskCompletionSource = new TaskCompletionSource();

            AddToRenderQueue(elements =>
            {
                try
                {
                    action((elements.RunLoop, elements.FrontEnd, elements.Map)!);
                    taskCompletionSource.SetResult();
                }
                catch (Exception ex)
                {
                    taskCompletionSource.SetException(ex);
                }
            });

            return taskCompletionSource.Task;
        }

        public void Invoke(Action<(RunLoop RunLoop, HeadlessFrontend FrontEnd, Map Map)> action)
        {
            try
            {
                InvokeAsync(action).Wait();
            }
            catch (AggregateException ex)
            {
                throw ex.InnerException!;
            }
        }

        public void Dispose()
        {
            if (!_Disposed)
            {
                _Disposed = true;
                _CancellationTokenSource.Cancel();
                _CancellationTokenSource.Dispose();
                _RendererThread.Join();
            }
        }
    }

    public class RendererPool : IDisposable
    {
        #region Private fields
        private bool _Disposed = false;
        private readonly object _Lock = new();
        private readonly int _MinRenderers;
        private readonly int _MaxRenderers;
        private readonly Func<(RunLoop RunLoop, HeadlessFrontend FrontEnd, Map Map)> _RendererInitializer;
        private readonly BlockingCollection<Renderer> _IdleRenderers = new(new ConcurrentBag<Renderer>());
        private int _TotalRenderers;
        private readonly Timer _RemoveRendererTimer;
        #endregion

        #region Constructor
        public RendererPool(int minRenderers, int maxRenderers, Func<(RunLoop RunLoop, HeadlessFrontend FrontEnd, Map Map)> rendererInitializer)
        {
            _MinRenderers = minRenderers;
            _MaxRenderers = maxRenderers;
            _RendererInitializer = rendererInitializer;
            _RemoveRendererTimer = new Timer(ReduceRenderers, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            _TotalRenderers = 0;

            lock (_Lock)
            {
                Task.WaitAll(
                    Enumerable.Range(0, _MinRenderers)
                    .Select(_ => Task.Run(() => _IdleRenderers.Add(new Renderer(_RendererInitializer))))
                    .ToArray()
                );

                _TotalRenderers = _MinRenderers;
            }
        }
        #endregion

        #region Private methods
        private void ReduceRenderers(object? state)
        {
            lock (_Lock)
            {
                if (_IdleRenderers.Count == _TotalRenderers)
                {
                    Task.WaitAll(
                        Enumerable.Range(0, _TotalRenderers - _MinRenderers)
                        .Select(_ => Task.Run(() => _IdleRenderers.Take().Dispose()))
                        .ToArray()
                    );

                    _TotalRenderers = _MinRenderers;
                }
            }
        }
        #endregion

        #region Public methods
        public Renderer Acquire()
        {
            lock (_Lock)
            {
                _RemoveRendererTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

                if (!_IdleRenderers.Any() && _TotalRenderers < _MaxRenderers)
                {
                    _TotalRenderers++;
                    return new Renderer(_RendererInitializer);
                }
            }

            return _IdleRenderers.Take();
        }

        public void Release(Renderer renderer)
        {
            lock (_Lock)
            {
                if (_Disposed)
                {
                    renderer.Dispose();
                    _TotalRenderers--;
                }
                else
                {
                    _IdleRenderers.Add(renderer);
                    _RemoveRendererTimer.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
                }
            }
        }

        public void Dispose()
        {
            if (!_Disposed)
            {
                _Disposed = true;

                lock (_Lock)
                {
                    _RemoveRendererTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

                    while (_IdleRenderers.Any())
                    {
                        var renderer = _IdleRenderers.Take();
                        renderer.Dispose();

                        _TotalRenderers--;
                    }
                }
            }
        }
        #endregion
    }

    public class ServeRendered
    {
        public static void Init(Configuration configuration, ILogger logger, IHostApplicationLifetime lifetime, IApplicationBuilder applicationBuilder, IEndpointRouteBuilder endpointRouteBuilder, string routePrefix)
        {
            var tileRendererPools = new Dictionary<string, Dictionary<int, RendererPool>>();
            var staticRendererPools = new Dictionary<string, Dictionary<int, RendererPool>>();
            var sourceFromDataRegex = new Regex(@"^local://data/(?<source>[^\.]+).json$");
            var localSpriteRegex = new Regex(@"^local://styles/(?<style>[^/]+)/sprite$");
            var localFontsRegex = new Regex(@"^local://fonts/(?<path>.+)$");
            var internalTileMargin = Math.Max(configuration.Options!.TileMargin, Math.Max((Util.InternalTileSize - Util.TileSize) / 2, 0));
            var mapSize = Util.TileSize + internalTileMargin * 2;
            var serveBounds = new[]
            {
                Math.Min(configuration.Options.ServeBounds[0], configuration.Options.ServeBounds[2]),
                Math.Min(configuration.Options.ServeBounds[1], configuration.Options.ServeBounds[3]),
                Math.Max(configuration.Options.ServeBounds[0], configuration.Options.ServeBounds[2]),
                Math.Max(configuration.Options.ServeBounds[1], configuration.Options.ServeBounds[3])
            };

            foreach (var styleEntry in configuration.Styles)
            {
                var styleJSON = styleEntry.Value.StyleJSON.Deserialize<JsonNode>();

                if (styleJSON!["sources"] != null)
                {
                    foreach (var source in styleJSON["sources"]!.AsObject())
                    {
                        if (source.Value!["url"] != null)
                        {
                            var match = sourceFromDataRegex.Match(source.Value["url"]!.GetValue<string>());

                            if (match.Success)
                            {
                                source.Value!["url"] = $"mbtiles://{Path.Combine(configuration.Options!.Paths!.MBTiles, configuration.Data[match.Groups["source"].Value].MBTiles)}";
                            }
                        }
                    }
                }

                if (styleJSON["sprite"] != null)
                {
                    var match = localSpriteRegex.Match(styleJSON["sprite"]!.GetValue<string>());

                    if (match.Success)
                    {
                        styleJSON["sprite"] = $"file://{Path.Combine(configuration.Options!.Paths!.Sprites, configuration.Styles[match.Groups["style"].Value].SpritePath)}";
                    }
                }

                if (styleJSON["glyphs"] != null)
                {
                    var match = localFontsRegex.Match(styleJSON["glyphs"]!.GetValue<string>());

                    if (match.Success)
                    {
                        styleJSON["glyphs"] = $"file://{Path.Combine(configuration.Options!.Paths!.Fonts, match.Result("${path}"))}";
                    }
                }

                var styleJSONString = styleJSON.ToJsonString();

                tileRendererPools.Add(
                    key: styleEntry.Key,
                    value: Enumerable.Range(1, Math.Min(configuration.Options!.MaxScaleFactor, 9))
                        .ToDictionary(
                            scale => scale,
                            scale => new RendererPool(
                                minRenderers: (scale <= configuration.Options.MinRendererPoolSizes.Length) ? configuration.Options.MinRendererPoolSizes[scale - 1] : configuration.Options.MinRendererPoolSizes.Last(),
                                maxRenderers: (scale <= configuration.Options.MaxRendererPoolSizes.Length) ? configuration.Options.MaxRendererPoolSizes[scale - 1] : configuration.Options.MaxRendererPoolSizes.Last(),
                                rendererInitializer: () =>
                                {
                                    var size = new Size((uint)mapSize, (uint)mapSize);
                                    var runLoop = new RunLoop();
                                    var frontend = new HeadlessFrontend(size, scale);
                                    var map = new Map(frontend, new MapObserver(), new MapOptions().WithMapMode(MapMode.Static).WithConstrainMode(ConstrainMode.None).WithSize(size).WithPixelRatio(scale));

                                    map.Style.LoadJSON(styleJSONString);

                                    return (runLoop, frontend, map);
                                }
                            )
                        )
                );

                lifetime.ApplicationStopping.Register(() =>
                {
                    foreach (var rendererPool in tileRendererPools.Values.SelectMany(g => g.Values).ToArray())
                    {
                        rendererPool.Dispose();
                    }
                });

                if (configuration.Options.ServeStaticMaps)
                {
                    staticRendererPools.Add(
                        key: styleEntry.Key,
                        value: Enumerable.Range(1, Math.Min(configuration.Options!.MaxScaleFactor, 9))
                            .ToDictionary(
                                scale => scale,
                                scale => new RendererPool(
                                    minRenderers: (scale <= configuration.Options.MinRendererPoolSizes.Length) ? configuration.Options.MinRendererPoolSizes[scale - 1] : configuration.Options.MinRendererPoolSizes.Last(),
                                    maxRenderers: (scale <= configuration.Options.MaxRendererPoolSizes.Length) ? configuration.Options.MaxRendererPoolSizes[scale - 1] : configuration.Options.MaxRendererPoolSizes.Last(),
                                    rendererInitializer: () =>
                                    {
                                        var size = new Size((uint)Util.TileSize, (uint)Util.TileSize);
                                        var runLoop = new RunLoop();
                                        var frontend = new HeadlessFrontend(size, scale);
                                        var map = new Map(frontend, new MapObserver(), new MapOptions().WithMapMode(MapMode.Static).WithConstrainMode(ConstrainMode.None).WithSize(size).WithPixelRatio(scale));

                                        map.Style.LoadJSON(styleJSONString);

                                        return (runLoop, frontend, map);
                                    }
                                )
                            )
                    );
                }
            }

            var respondImage = (string format, SKBitmap image) =>
            {
                using var stream = new MemoryStream();
                var encodedFormat = SKEncodedImageFormat.Png;
                var encoderQuality = configuration.Options.FormatQuality["png"];
                var contentType = "image/png";

                switch (format)
                {
                    case "jpg":
                    case "jpeg":
                        encodedFormat = SKEncodedImageFormat.Jpeg;
                        contentType = MediaTypeNames.Image.Jpeg;
                        encoderQuality = configuration.Options.FormatQuality["jpeg"];
                        break;
                    case "png":
                        break;
                    case "webp":
                        encodedFormat = SKEncodedImageFormat.Webp;
                        encoderQuality = configuration.Options.FormatQuality["webp"];
                        contentType = "image/webp";
                        break;
                    default:
                        return Results.BadRequest("Invalid format");
                }

                image.Encode(stream, encodedFormat, encoderQuality);

                return Results.Bytes(stream.ToArray(), contentType);
            };

            applicationBuilder.UseRewriter(new RewriteOptions().AddRewrite(
                regex: $@"^{routePrefix.Substring(1)}/(?<id>[^/]+)/(?<z>\d{{1,2}})/(?<x>\d{{1,20}})/(?<y>\d{{1,20}})\.(?<format>.+)$",
                replacement: $"{routePrefix}/${{id}}/${{z}}/${{x}}/${{y}}@1x.${{format}}",
                skipRemainingRules: true
            ));

            endpointRouteBuilder.MapGet(
                pattern: $@"/{{id:regex(^[A-Za-z0-9_\-]+$)}}/{{z:int:min(0)}}/{{x:int:min(0)}}/{{y:int:min(0)}}@{{scale:int:min(1):max({Math.Min(configuration.Options!.MaxScaleFactor, 9)})}}x.{{format:regex(^\w+$)}}",
                handler: async (HttpContext context, string id, int x, int y, byte z, int scale, string format) =>
                {
                    if (id == null || !configuration.Styles.ContainsKey(id))
                    {
                        return Results.NotFound();
                    }

                    if (
                        z < 0
                        || z > 22
                        || x < Util.LongitudeToTileX(serveBounds[0], z)
                        || x > Util.LongitudeToTileX(serveBounds[2], z)
                        || y < Util.LatitudeToTileY(serveBounds[3], z)
                        || y > Util.LatitudeToTileY(serveBounds[1], z)
                    )
                    {
                        return Results.BadRequest("Out of bounds");
                    }

                    var internalZoom = z + Math.Log2((double)Util.TileSize / Util.InternalTileSize);

                    PremultipliedImage? rawImage = null;

                    await Task.Run(async () =>
                    {
                        var renderer = tileRendererPools[id][scale].Acquire();

                        try
                        {
                            await renderer.InvokeAsync(elements =>
                            {
                                Exception? ex = null;

                                elements.Map.RenderStill(
                                    camera: elements.Map.CameraForLatLngBounds(new LatLngBounds(new CanonicalTileID(z, (uint)x, (uint)y)), new EdgeInsets(internalTileMargin, internalTileMargin, internalTileMargin, internalTileMargin)).WithZoom(internalZoom),
                                    debugOptions: MapDebugOptions.NoDebug,
                                    callback: exception =>
                                    {
                                        try
                                        {
                                            ex = exception;

                                            if (ex == null)
                                            {
                                                rawImage = elements.FrontEnd.ReadStillImage();
                                            }
                                        }
                                        finally
                                        {
                                            elements.RunLoop.Stop();
                                        }
                                    }
                                );

                                elements.RunLoop.Run();

                                if (ex != null)
                                {
                                    throw ex;
                                }
                            });
                        }
                        finally
                        {
                            tileRendererPools[id][scale].Release(renderer);
                        }
                    });

                    if (rawImage != null)
                    {
                        var internalImage = new SKBitmap();
                        internalImage.InstallPixels(
                            info: new SKImageInfo(
                                width: mapSize * scale,
                                height: mapSize * scale,
                                colorType: SKColorType.Rgba8888,
                                alphaType: SKAlphaType.Premul
                            ),
                            pixels: rawImage.Data
                        );

                        var image = internalImage;

                        if (internalTileMargin > 0)
                        {
                            image = new SKBitmap();

                            if (internalZoom >= 0)
                            {
                                internalImage.ExtractSubset(
                                    destination: image,
                                    subset: new SKRectI()
                                    {
                                        Location = new SKPointI(internalTileMargin * scale, internalTileMargin * scale),
                                        Size = new SKSizeI(Util.TileSize * scale, Util.TileSize * scale)
                                    }
                                );
                            }
                            else
                            {
                                var tileSize = Util.TileSize * (1 << -(int)internalZoom);

                                internalImage.ExtractSubset(
                                    destination: image,
                                    subset: new SKRectI()
                                    {
                                        Location = new SKPointI((mapSize - tileSize) / 2, (mapSize - tileSize) / 2),
                                        Size = new SKSizeI(tileSize, tileSize)
                                    }
                                );

                                image = image.Resize(new SKSizeI(Util.TileSize, Util.TileSize), SKFilterQuality.High);
                            }
                        }

                        return respondImage(format, image);
                    }

                    return Results.StatusCode(StatusCodes.Status500InternalServerError);
                }
            );

            if (configuration.Options.ServeStaticMaps)
            {
                var pathRegex = new Regex(@"^(((?<propertyName>latlng)(?<propertyValue>)\|)|((?<propertyName>fill|stroke|width|linecap|linejoin|border|borderWidth):(?<propertyValue>[^\|]+)\|)){0,8}((enc:(?<encodedPoints>.+))|(((?<lon>[+-]?(\d{0,20}\.)?\d{1,20}),(?<lat>[+-]?(\d{0,20}\.)?\d{1,20})\|)+(?<lon>[+-]?(\d{0,20}\.)?\d{1,20}),(?<lat>[+-]?(\d{0,20}\.)?\d{1,20})))$");
                var markerRegex = new Regex(@"^(?<lon>[+-]?(\d{0,20}\.)?\d{1,20}),(?<lat>[+-]?(\d{0,20}\.)?\d{1,20})\|(?<iconPath>[^\|]+)(\|((?<optionName>scale):(?<optionValue>(\d{0,20}\.)?\d{1,20})|(?<optionName>offset):(?<optionValue>(?<offsetX>[+-]?\d+),(?<offsetY>[+-]?\d+)))){0,2}$");

                applicationBuilder.UseRewriter(
                    new RewriteOptions()
                    .AddRewrite(
                        regex: $@"^{routePrefix.Substring(1)}/(?<id>[^/]+)/static(/(?<raw>raw))?/((?<lon>[+-]?(\d{{0,20}}\.)?\d{{1,20}}),(?<lat>[+-]?(\d{{0,20}}\.)?\d{{1,20}}),(?<zoom>(\d{{0,20}}\.)?\d{{1,20}})(@(?<bearing>[+-]?(\d{{0,20}}\.)?\d{{1,20}})(,(?<pitch>[+-]?(\d{{0,20}}\.)?\d{{1,20}}))?)?|(?<minx>[+-]?(\d{{0,20}}\.)?\d{{1,20}}),(?<miny>[+-]?(\d{{0,20}}\.)?\d{{1,20}}),(?<maxx>[+-]?(\d{{0,20}}\.)?\d{{1,20}}),(?<maxy>[+-]?(\d{{0,20}}\.)?\d{{1,20}})|(?<auto>auto))/(?<imageWidth>\d{{1,20}})x(?<imageHeight>\d{{1,20}})(@(?<scale>\d)x)?\.(?<format>\w{{1,20}})$",
                        replacement: $@"{routePrefix.Substring(1)}/${{id}}/static?{string.Join("&", new[] { "raw", "lon", "lat", "zoom", "bearing", "pitch", "minx", "miny", "maxx", "maxy", "auto", "imageWidth", "imageHeight", "scale", "format" }.Select(parameter => string.Format("{0}=${{{0}}}", parameter)))}",
                        skipRemainingRules: true
                    )
                );

                endpointRouteBuilder.MapGet("/{id:regex(^[A-Za-z0-9_\\-]+$)}/static/{*invalid}", () => Results.BadRequest());

                applicationBuilder.Use((context, next) =>
                {
                    if (context.Request.Path.StartsWithSegments(routePrefix) && context.Request.Path.ToString().EndsWith("/static") && context.Request.Query.Any())
                    {
                        context.Request.QueryString =
                            new QueryBuilder(
                                context.Request.Query
                                .Where(entry => !string.IsNullOrWhiteSpace(entry.Value) || entry.Key == "raw" || entry.Key == "auto")
                                .Select(entry =>
                                {
                                    if (entry.Key == "raw" || entry.Key == "auto")
                                    {
                                        return new KeyValuePair<string, StringValues>(entry.Key, (!string.IsNullOrWhiteSpace(entry.Value)).ToString());
                                    }
                                    
                                    return entry;
                                })
                            ).ToQueryString();
                    }
                    
                    return next(context);
                });

                endpointRouteBuilder.MapGet(
                    pattern: "/{id:regex(^[A-Za-z0-9_\\-]+$)}/static",
                    handler:
                    async (
                        HttpContext context,
                        string id,
                        [FromQuery(Name = "raw")] bool raw,
                        [FromQuery(Name = "lon")] double? lon,
                        [FromQuery(Name = "lat")] double? lat,
                        [FromQuery(Name = "zoom")] double? zoom,
                        [FromQuery(Name = "bearing")] double? bearing,
                        [FromQuery(Name = "pitch")] double? pitch,
                        [FromQuery(Name = "minx")] double? minx,
                        [FromQuery(Name = "miny")] double? miny,
                        [FromQuery(Name = "maxx")] double? maxx,
                        [FromQuery(Name = "maxy")] double? maxy,
                        [FromQuery(Name = "auto")] bool auto,
                        [FromQuery(Name = "imageWidth")] int width,
                        [FromQuery(Name = "imageHeight")] int height,
                        [FromQuery(Name = "scale")] int? scale,
                        [FromQuery(Name = "format")] string format,
                        [FromQuery(Name = "path")] string[]? rawPaths,
                        [FromQuery(Name = "marker")] string[]? rawMarkers,
                        [FromQuery(Name = "padding")] double? padding,
                        [FromQuery(Name = "maxzoom")] double? maxzoom,
                        [FromQuery(Name = "fill")] string? fill,
                        [FromQuery(Name = "stroke")] string? stroke,
                        [FromQuery(Name = "width")] double? strokeWidth,
                        [FromQuery(Name = "linecap")] string? linecap,
                        [FromQuery(Name = "linejoin")] string? linejoin,
                        [FromQuery(Name = "border")] string? border,
                        [FromQuery(Name = "borderWidth")] double? borderWidth
                    ) =>
                    {
                        var validData = false;
                        List<Dictionary<string, object>>? paths = null;
                        List<Dictionary<string, object>>? markers = null;
                        bearing ??= 0;
                        pitch ??= 0;
                        scale ??= 1;
                        padding ??= 0.1;
                        maxzoom ??= 22;

                        if (id == null || !configuration.Styles.ContainsKey(id) || scale.Value < 1 || scale.Value > Math.Min(configuration.Options!.MaxScaleFactor, 9))
                        {
                            return Results.NotFound();
                        }

                        if (width <= 0 || height <= 0 || width > configuration.Options.MaxSize || height > configuration.Options.MaxSize)
                        {
                            return Results.BadRequest("Invalid size");
                        }

                        if (rawPaths!.Any())
                        {
                            try
                            {
                                paths = new List<Dictionary<string, object>>();

                                foreach (var rawPath in rawPaths!)
                                {
                                    var pathMatch = pathRegex.Match(rawPath);

                                    if (pathMatch.Success)
                                    {
                                        paths.Add(
                                            pathMatch.Groups["propertyName"].Captures
                                            .Zip(pathMatch.Groups["propertyValue"].Captures)
                                            .Select(tuple => (Name: tuple.First.Value, Value: (object)tuple.Second.Value))
                                            .Concat(
                                                new[]
                                                {
                                                (
                                                    Name: "points",
                                                    Value:
                                                        (object)(
                                                        pathMatch.Groups["encodedPoints"].Success
                                                        ?
                                                        Util.Decode(pathMatch.Groups["encodedPoints"].Value).ToArray()
                                                        :
                                                        pathMatch.Groups["lon"].Captures.Zip(pathMatch.Groups["lat"].Captures)
                                                        .Select(
                                                            tuple =>
                                                                pathMatch.Groups["propertyName"].Captures.Any(capture => capture.Value == "latlng")
                                                                ?
                                                                new[] { double.Parse(tuple.Second.Value, CultureInfo.InvariantCulture), double.Parse(tuple.First.Value, CultureInfo.InvariantCulture) }
                                                                :
                                                                new[] { double.Parse(tuple.First.Value, CultureInfo.InvariantCulture), double.Parse(tuple.Second.Value, CultureInfo.InvariantCulture) }
                                                        )
                                                        .SelectMany(point =>
                                                        {
                                                            if (raw)
                                                            {
                                                                Reproject.ReprojectPoints(point, null, ProjectionInfo.FromEpsgCode(3857), ProjectionInfo.FromEpsgCode(4326), 0, 1);
                                                            }

                                                            return point;
                                                        })
                                                        .ToArray()
                                                        )
                                                )
                                                }
                                            )
                                            .ToDictionary(tuple => tuple.Name, tuple => tuple.Value)
                                        );
                                    }
                                }
                            }
                            catch
                            {
                                return Results.BadRequest("Invalid path data");
                            }
                        }

                        if (rawMarkers!.Any())
                        {
                            try
                            {
                                markers = new List<Dictionary<string, object>>();

                                foreach (var rawMarker in rawMarkers!)
                                {
                                    var markerMatch = markerRegex.Match(rawMarker);

                                    if (markerMatch.Success)
                                    {
                                        if (Util.HttpRegex.IsMatch(markerMatch.Groups["iconPath"].Value) && !configuration.Options.AllowRemoteMarkerIcons)
                                        {
                                            continue;
                                        }

                                        var marker = new Dictionary<string, object>();

                                        var point = new[]
                                        {
                                        double.Parse(markerMatch.Groups["lon"].Value, CultureInfo.InvariantCulture),
                                        double.Parse(markerMatch.Groups["lat"].Value, CultureInfo.InvariantCulture)
                                    };

                                        if (raw)
                                        {
                                            Reproject.ReprojectPoints(point, null, ProjectionInfo.FromEpsgCode(3857), ProjectionInfo.FromEpsgCode(4326), 0, 1);
                                        }

                                        marker.Add("point", point);
                                        marker.Add("iconPath", markerMatch.Groups["iconPath"].Value);

                                        foreach (var option in markerMatch.Groups["optionName"].Captures.Zip(markerMatch.Groups["optionValue"].Captures))
                                        {
                                            if (option.First.Value == "scale")
                                            {
                                                marker.Add(option.First.Value, double.Parse(option.Second.Value, CultureInfo.InvariantCulture));
                                            }

                                            if (option.First.Value == "offset")
                                            {
                                                marker.Add(option.First.Value, new[] { int.Parse(markerMatch.Groups["offsetX"].Value, CultureInfo.InvariantCulture), int.Parse(markerMatch.Groups["offsetY"].Value, CultureInfo.InvariantCulture) });
                                            }
                                        }

                                        markers.Add(marker);
                                    }
                                }
                            }
                            catch
                            {
                                return Results.BadRequest("Invalid marker data");
                            }
                        }

                        if (lon.HasValue && lat.HasValue && zoom.HasValue)
                        {
                            bearing ??= 0;
                            pitch ??= 0;

                            if (raw)
                            {
                                var point = new[] { lon.Value, lat.Value };

                                Reproject.ReprojectPoints(point, null, ProjectionInfo.FromEpsgCode(3857), ProjectionInfo.FromEpsgCode(4326), 0, 1);

                                lon = point[0];
                                lat = point[1];
                            }

                            if (
                                lon.Value < serveBounds[0]
                                || lon.Value > serveBounds[2]
                                || lat.Value < serveBounds[1]
                                || lat.Value > serveBounds[3]
                            )
                            {
                                return Results.BadRequest("Out of bounds");
                            }

                            validData = true;
                        }
                        else if (minx.HasValue && miny.HasValue && maxx.HasValue && maxy.HasValue)
                        {
                            if (raw)
                            {
                                var boudingBox = new[] { minx.Value, miny.Value, maxx.Value, maxy.Value };
                                Reproject.ReprojectPoints(boudingBox, null, ProjectionInfo.FromEpsgCode(3857), ProjectionInfo.FromEpsgCode(4326), 0, 2);
                            }

                            if (
                                minx.Value >= serveBounds[2]
                                || maxx.Value <= serveBounds[0]
                                || miny.Value >= serveBounds[3]
                                || maxy.Value <= serveBounds[1]
                            )
                            {
                                return Results.BadRequest("Out of bounds");
                            }

                            minx = Math.Max(minx.Value, serveBounds[0]);
                            maxx = Math.Min(maxx.Value, serveBounds[2]);
                            miny = Math.Max(miny.Value, serveBounds[1]);
                            maxy = Math.Min(maxy.Value, serveBounds[3]);

                            lon = (minx.Value + maxx.Value) / 2.0;
                            lat = (miny.Value + maxy.Value) / 2.0;
                            zoom = Math.Min(Util.CalculateZoomForBoundingBox(minx.Value, miny.Value, maxx.Value, maxy.Value, width, height, padding.Value), maxzoom.Value);

                            validData = true;
                        }
                        else if (auto && (rawPaths!.Any() || rawMarkers!.Any()))
                        {
                            var coords = (paths?.Select(path => path["points"]) ?? Array.Empty<object>()).Concat(markers?.Select(marker => marker["point"]) ?? Array.Empty<object>()).OfType<double[]>().SelectMany(points => points);

                            if (!coords.Any())
                            {
                                return Results.BadRequest("Not enough data to compute bounds");
                            }

                            minx = coords.Where((value, index) => index % 2 == 0).Min();
                            miny = coords.Where((value, index) => index % 2 == 1).Min();
                            maxx = coords.Where((value, index) => index % 2 == 0).Max();
                            maxy = coords.Where((value, index) => index % 2 == 1).Max();

                            if (
                                minx.Value >= serveBounds[2]
                                || maxx.Value <= serveBounds[0]
                                || miny.Value >= serveBounds[3]
                                || maxy.Value <= serveBounds[1]
                            )
                            {
                                return Results.BadRequest("Out of bounds");
                            }

                            lon = (minx.Value + maxx.Value) / 2.0;
                            lat = (miny.Value + maxy.Value) / 2.0;
                            zoom = Math.Min(Util.CalculateZoomForBoundingBox(minx.Value, miny.Value, maxx.Value, maxy.Value, width, height, padding.Value), maxzoom.Value);

                            validData = true;
                        }

                        if (validData)
                        {
                            var renderer = staticRendererPools[id][scale.Value].Acquire();

                            PremultipliedImage? rawImage = null;

                            try
                            {
                                await renderer.InvokeAsync(elements =>
                                {
                                    Exception? ex = null;

                                    elements.FrontEnd.Size = new Size((uint)width, (uint)height);
                                    elements.Map.SetSize(elements.FrontEnd.Size);

                                    elements.Map.RenderStill(
                                        camera: new CameraOptions()
                                            .WithCenter(new LatLng(lat!.Value, lon!.Value))
                                            .WithZoom(zoom!.Value)
                                            .WithBearing(bearing.Value)
                                            .WithPitch(pitch.Value),
                                        debugOptions: MapDebugOptions.NoDebug,
                                        callback: exception =>
                                        {
                                            try
                                            {
                                                ex = exception;

                                                if (ex == null)
                                                {
                                                    rawImage = elements.FrontEnd.ReadStillImage();
                                                }
                                            }
                                            finally
                                            {
                                                elements.RunLoop.Stop();
                                            }
                                        }
                                    );

                                    elements.RunLoop.Run();

                                    if (paths != null)
                                    {
                                        foreach (var path in paths)
                                        {
                                            path["points"] = ((double[])path["points"])
                                                .Chunk(2)
                                                .Select(p => elements.Map.TransformState.LatLngToScreenCoordinate(new LatLng(p[1], p[0])))
                                                .SelectMany(p => new[] { p.X, height - p.Y })
                                                .ToArray();
                                        }
                                    }

                                    if (markers != null)
                                    {
                                        foreach (var marker in markers)
                                        {
                                            marker["point"] = ((double[])marker["point"])
                                                .Chunk(2)
                                                .Select(p => elements.Map.TransformState.LatLngToScreenCoordinate(new LatLng(p[1], p[0])))
                                                .SelectMany(p => new[] { p.X, height - p.Y })
                                                .ToArray();
                                        }
                                    }

                                    if (ex != null)
                                    {
                                        throw ex;
                                    }
                                });
                            }
                            finally
                            {
                                staticRendererPools[id][scale.Value].Release(renderer);
                            }

                            if (rawImage != null)
                            {
                                var image = new SKBitmap();
                                var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                                image.InstallPixels(info, rawImage!.Data);

                                if ((paths?.Any() ?? false) || (markers?.Any() ?? false))
                                {
                                    using var canvas = new SKCanvas(image);

                                    if (paths?.Any() ?? false)
                                    {
                                        var globalFill = new SKColor(255, 255, 255, 102);

                                        if (SKColor.TryParse(fill, out SKColor globalFillValue))
                                        {
                                            globalFill = globalFillValue;
                                        }

                                        var globalStroke = new SKColor(0, 64, 255, 178);

                                        if (SKColor.TryParse(stroke, out SKColor globalStrokeValue))
                                        {
                                            globalStroke = globalStrokeValue;
                                        }

                                        var globalBorder = SKColors.Transparent;

                                        if (SKColor.TryParse(border, out SKColor globalBorderValue))
                                        {
                                            globalBorder = globalBorderValue;
                                        }

                                        var globalStrokeCap = SKStrokeCap.Butt;

                                        if (Enum.TryParse(linecap, true, out SKStrokeCap globalStrokeCapValue) && Enum.IsDefined(globalStrokeCapValue))
                                        {
                                            globalStrokeCap = globalStrokeCapValue;
                                        }

                                        var globalStrokeJoin = SKStrokeJoin.Miter;

                                        if (Enum.TryParse(linejoin, true, out SKStrokeJoin globalStrokeJoinValue) && Enum.IsDefined(globalStrokeJoinValue))
                                        {
                                            globalStrokeJoin = globalStrokeJoinValue;
                                        }

                                        foreach (var path in paths)
                                        {
                                            using (var skPath = new SKPath())
                                            {
                                                var points = (double[])path["points"];

                                                skPath.MoveTo((float)points[0], (float)points[1]);

                                                for (int i = 2; i < points.Length; i += 2)
                                                {
                                                    skPath.LineTo((float)points[i], (float)points[i + 1]);
                                                }

                                                if (points[0] == points[points.Length - 2] && points[1] == points[points.Length - 1])
                                                {
                                                    skPath.Close();
                                                }

                                                var skPaint = new SKPaint() { IsAntialias = true };

                                                if (!string.IsNullOrWhiteSpace(fill) || path.ContainsKey("fill"))
                                                {
                                                    var pathFill = globalFill;

                                                    if (SKColor.TryParse(path!.GetValueOrDefault("fill")?.ToString(), out SKColor pathFillValue))
                                                    {
                                                        pathFill = pathFillValue;
                                                    }

                                                    skPaint.IsStroke = false;
                                                    skPaint.Color = pathFill;

                                                    canvas.DrawPath(skPath, skPaint);
                                                }

                                                var pathStrokeWidth = strokeWidth ?? 0;
                                                double.TryParse(path.GetValueOrDefault("width")?.ToString(), out pathStrokeWidth);

                                                if (pathStrokeWidth <= 0 && string.IsNullOrWhiteSpace(fill) && !path.ContainsKey("fill"))
                                                {
                                                    pathStrokeWidth = 1;
                                                }

                                                if (pathStrokeWidth > 0)
                                                {
                                                    skPaint.IsStroke = true;

                                                    var pathStrokeCap = globalStrokeCap;

                                                    if (Enum.TryParse(path.GetValueOrDefault("linecap")?.ToString(), true, out SKStrokeCap pathStrokeCapValue) && Enum.IsDefined(pathStrokeCapValue))
                                                    {
                                                        pathStrokeCap = pathStrokeCapValue;
                                                    }

                                                    skPaint.StrokeCap = pathStrokeCap;

                                                    var pathStrokeJoin = globalStrokeJoin;

                                                    if (Enum.TryParse(path.GetValueOrDefault("linecap")?.ToString(), true, out SKStrokeJoin pathStrokeJoinValue) && Enum.IsDefined(pathStrokeJoinValue))
                                                    {
                                                        pathStrokeJoin = pathStrokeJoinValue;
                                                    }

                                                    skPaint.StrokeJoin = pathStrokeJoin;

                                                    var pathBorderWidth = borderWidth ?? pathStrokeWidth * 0.1;
                                                    double.TryParse(path.GetValueOrDefault("borderWidth")?.ToString(), out pathBorderWidth);

                                                    if (pathBorderWidth > 0 && !string.IsNullOrWhiteSpace(border) || path.ContainsKey("border"))
                                                    {
                                                        var pathBorder = globalBorder;

                                                        if (SKColor.TryParse(path.GetValueOrDefault("border")?.ToString(), out SKColor pathBorderValue))
                                                        {
                                                            pathBorder = pathBorderValue;
                                                        }

                                                        skPaint.Color = pathBorder;
                                                        skPaint.StrokeWidth = (float)(pathStrokeWidth + pathBorderWidth * 2);

                                                        canvas.DrawPath(skPath, skPaint);
                                                    }

                                                    var pathStroke = globalStroke;

                                                    if (SKColor.TryParse(path.GetValueOrDefault("stroke")?.ToString(), out SKColor pathStrokeValue))
                                                    {
                                                        pathStroke = pathStrokeValue;
                                                    }

                                                    skPaint.Color = pathStroke;
                                                    skPaint.StrokeWidth = (float)pathStrokeWidth;

                                                    canvas.DrawPath(skPath, skPaint);
                                                }
                                            }
                                        }
                                    }

                                    if (markers?.Any() ?? false)
                                    {
                                        var paint = new SKPaint() { IsAntialias = true, FilterQuality = SKFilterQuality.High };

                                        foreach (var marker in markers)
                                        {
                                            var point = ((double[])marker["point"]).Chunk(2).Select(c => new SKPoint((float)c[0], (float)c[1])).First();
                                            var iconPath = (string)marker["iconPath"];


                                            SKBitmap? markerIcon = null;

                                            try
                                            {
                                                if (Util.HttpRegex.IsMatch(iconPath))
                                                {
                                                    using var client = new HttpClient();
                                                    markerIcon = SKBitmap.Decode(await client.GetByteArrayAsync(iconPath));
                                                }
                                                else
                                                {
                                                    markerIcon = SKBitmap.Decode(Path.Combine(configuration.Options.Paths!.Icons, iconPath));
                                                }
                                            }
                                            finally
                                            {

                                            }

                                            if (markerIcon == null)
                                            {
                                                continue;
                                            }

                                            var markerScale = (float)(double)marker.GetValueOrDefault("scale", 1.0);
                                            var markerOffset = ((int[])marker.GetValueOrDefault("offset", new[] { 0, 0 })).Chunk(2).Select(c => new SKPoint(c[0], c[1])).First();

                                            point += new SKPoint((-markerIcon.Width / 2 + markerOffset.X) * markerScale, (-markerIcon.Height + markerOffset.Y) * markerScale);

                                            var destRect = new SKRect()
                                            {
                                                Location = point,
                                                Size = new SKSize(markerIcon.Width * markerScale, markerIcon.Height * markerScale)
                                            };

                                            canvas.DrawBitmap(markerIcon, destRect, paint);
                                        }
                                    }

                                    canvas.Flush();
                                }

                                return respondImage(format, image);
                            }
                        }

                        return Results.StatusCode(StatusCodes.Status500InternalServerError);
                    }
                )
                .CacheOutput(builder => builder.Cache().SetVaryByQuery(new[]
                {
                    "raw",
                    "lon",
                    "lat",
                    "zoom",
                    "bearing",
                    "pitch",
                    "minx",
                    "miny",
                    "maxx",
                    "maxy",
                    "auto",
                    "imageWidth",
                    "imageHeight",
                    "scale",
                    "format",
                    "path",
                    "marker",
                    "padding",
                    "maxzoom",
                    "fill",
                    "stroke",
                    "width",
                    "linecap",
                    "linejoin",
                    "border",
                    "borderWidth"
                }));

                endpointRouteBuilder.MapGet("/{id:regex(^[A-Za-z0-9_\\-]+$)}.json", (HttpContext context, string id) =>
                {
                    if (!configuration.Styles.ContainsKey(id))
                    {
                        return Results.NotFound();
                    }

                    var tileJSON = configuration.Styles[id].TileJSON.Deserialize<JsonNode>();

                    tileJSON!["tiles"] = new JsonArray(new[] { JsonValue.Create(string.Format("{0}styles/{1}/{{z}}/{{x}}/{{y}}.{2}", Util.GetPublicUrl(context.Request), id, tileJSON["format"])) });

                    return Results.Json(tileJSON);
                });
            }
        }
    }
}
