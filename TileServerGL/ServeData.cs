using Mapbox.Vector.Tile;
using MaplibreNative;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TileServerGL
{
    public class FileProvider : IDisposable
    {
        private Thread _FileProviderThread;
        private bool _Disposed = false;
        private RunLoop? _RunLoop = null;
        private FileSource? _FileSource = null;
        private BlockingCollection<Action<(RunLoop RunLoop, FileSource FileSource)>> _FileRequestQueue = new();
        private CancellationTokenSource _CancellationTokenSource = new();

        public FileProvider(Func<(RunLoop RunLoop, FileSource FileSource)> initializer)
        {
            _FileProviderThread = new Thread(() =>
            {
                Thread.CurrentThread.Name = $"FileProvider {Environment.CurrentManagedThreadId}";

                (_RunLoop, _FileSource) = initializer();

                try
                {
                    while (_FileRequestQueue.TryTake(out Action<(RunLoop RunLoop, FileSource FileSource)>? action, Timeout.Infinite, _CancellationTokenSource.Token))
                    {
                        try
                        {
                            action((_RunLoop, _FileSource));
                        }
                        finally
                        {
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }

                _RunLoop.Dispose();
            });

            if (OperatingSystem.IsWindows())
            {
                _FileProviderThread.SetApartmentState(ApartmentState.STA);
            }

            _FileProviderThread.Start();
        }

        public void AddToFileRequestQueue(Action<(RunLoop RunLoop, FileSource FileSource)> action)
        {
            _FileRequestQueue.Add(action);
        }

        public Task InvokeAsync(Action<(RunLoop RunLoop, FileSource FileSource)> action)
        {
            var taskCompletionSource = new TaskCompletionSource();

            AddToFileRequestQueue(elements =>
            {
                try
                {
                    action((elements.RunLoop, elements.FileSource)!);
                    taskCompletionSource.SetResult();
                }
                catch (Exception ex)
                {
                    taskCompletionSource.SetException(ex);
                }
            });

            return taskCompletionSource.Task;
        }

        public void Invoke(Action<(RunLoop RunLoop, FileSource FileSource)> action)
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
                _FileProviderThread.Join();
            }
        }
    }

    public class FileProviderPool
    {
        #region Private fields
        private readonly object _Lock = new();
        private readonly int _MinFileProviders;
        private readonly int _MaxFileProviders;
        private readonly Func<(RunLoop RunLoop, FileSource FileSource)> _FileProviderInitializer;
        private readonly BlockingCollection<FileProvider> _IdleFileProviders = new(new ConcurrentBag<FileProvider>());
        private int _TotalFileProviders;
        private readonly Timer _RemoveFileProviderTimer;
        #endregion

        #region Constructor
        public FileProviderPool(int minFileProviders, int maxFileProviders, Func<(RunLoop RunLoop, FileSource FileSource)> fileProviderInitializer)
        {
            _MinFileProviders = minFileProviders;
            _MaxFileProviders = maxFileProviders;
            _FileProviderInitializer = fileProviderInitializer;
            _RemoveFileProviderTimer = new Timer(ReduceFileProviders, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            _TotalFileProviders = 0;

            lock (_Lock)
            {
                Task.WaitAll(
                    Enumerable.Range(0, _MinFileProviders)
                    .Select(_ => Task.Run(() => _IdleFileProviders.Add(new FileProvider(_FileProviderInitializer))))
                    .ToArray()
                );

                _TotalFileProviders = _MinFileProviders;
            }
        }
        #endregion

        #region Private methods
        private void ReduceFileProviders(object? state)
        {
            lock (_Lock)
            {
                if (_IdleFileProviders.Count == _TotalFileProviders)
                {
                    Task.WaitAll(
                        Enumerable.Range(0, _TotalFileProviders - _MinFileProviders)
                        .Select(_ => Task.Run(() => _IdleFileProviders.Take().Dispose()))
                        .ToArray()
                    );

                    _TotalFileProviders = _MinFileProviders;
                }
            }
        }
        #endregion

        #region Public methods
        public FileProvider Acquire()
        {
            lock (_Lock)
            {
                _RemoveFileProviderTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

                if (!_IdleFileProviders.Any() && _TotalFileProviders < _MaxFileProviders)
                {
                    _TotalFileProviders++;
                    return new FileProvider(_FileProviderInitializer);
                }
            }

            return _IdleFileProviders.Take();
        }

        public void Release(FileProvider fileProvider)
        {
            lock (_Lock)
            {
                _IdleFileProviders.Add(fileProvider);
                _RemoveFileProviderTimer.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
            }
        }
        #endregion
    }

    public class ServeData
    {
        private class LowerCaseNamingPolicy : JsonNamingPolicy
        {
            public override string ConvertName(string name) => name.ToLower();
        }

        public static void Init(Configuration configuration, IApplicationBuilder applicationBuilder, IEndpointRouteBuilder endpointRouteBuilder, string routePrefix)
        {
            var fileSource = FileSourceManager.GetFileSource(FileSourceType.Mbtiles, ResourceOptions.Default());
            var fileProviderPool = new FileProviderPool(0, 16, () => (new RunLoop(), fileSource));
            var lowerCaseNamingPolicy = new LowerCaseNamingPolicy();
            var serveBounds = new[]
            {
                Math.Min(configuration.Options!.ServeBounds[0], configuration.Options.ServeBounds[2]),
                Math.Min(configuration.Options.ServeBounds[1], configuration.Options.ServeBounds[3]),
                Math.Max(configuration.Options.ServeBounds[0], configuration.Options.ServeBounds[2]),
                Math.Max(configuration.Options.ServeBounds[1], configuration.Options.ServeBounds[3])
            };

            endpointRouteBuilder.MapGet(@"/{id:regex(^[A-Za-z0-9_\-]+$)}/{z:int:min(0)}/{x:int:min(0)}/{y:int:min(0)}.{format:regex(^\w+$)}", async (HttpContext context, string id, int x, int y, byte z, string format) =>
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

                var fileProvider = fileProviderPool.Acquire();

                try
                {
                    await fileProvider.InvokeAsync(elements =>
                    {
                        var request = elements.FileSource.Request(Resource.Tile(string.Format("mbtiles://{0}?tile={{z}}/{{x}}/{{y}}.{1}", Path.Combine(configuration.Options!.Paths!.MBTiles, data.MBTiles), tileJSONFormat), 1.0f, x, y, z, Resource.TilesetScheme.XYZ), res =>
                        {
                            response = res;
                            elements.RunLoop.Stop();
                        });

                        elements.RunLoop.Run();
                    });
                }
                finally
                {
                    fileProviderPool.Release(fileProvider);
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
