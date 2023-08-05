using HandlebarsDotNet;
using MaplibreNative;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileSystemGlobbing.Internal.PatternContexts;
using Microsoft.Extensions.Primitives;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TileServerGL
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            if (builder.Configuration.GetValue<bool?>("UseOutputCache") ?? false)
            {
                builder.Services.AddOutputCache(options =>
                {
                    options.DefaultExpirationTimeSpan = TimeSpan.FromDays(365);
                    options.SizeLimit = builder.Configuration.GetValue<long?>("MemoryCacheSize") ?? options.SizeLimit;
                    options.AddBasePolicy(
                        policyBuilder => policyBuilder.Cache()
                            .SetVaryByHost(true)
                            .SetVaryByRouteValue("*")
                            .SetVaryByQuery(Array.Empty<string>())
                    );
                });
            }

            builder.Services.AddCors(options =>
            {
                if (!string.IsNullOrWhiteSpace(builder.Configuration["AllowedOrigins"]))
                {
                    options.AddDefaultPolicy(policyBuilder => policyBuilder.WithOrigins(builder.Configuration["AllowedOrigins"]!.Split(";")));
                }
                else
                {
                    options.AddDefaultPolicy(policyBuilder => policyBuilder.AllowAnyOrigin());
                }

            });

            var app = builder.Build();

            app.UseCors();

            app.UseStaticFiles(new StaticFileOptions()
            {
                FileProvider = new PhysicalFileProvider(Path.Combine(builder.Environment.ContentRootPath, @"public\resources"))
            });

            Configuration? configuration = null;

            var configurationFile = Util.ResolvePath(builder.Configuration["ConfigurationFile"] ?? string.Empty);

            try
            {
                configuration = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(configurationFile), new JsonSerializerOptions()
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                configuration!.Options!.Paths!.Root = Util.ResolvePath(
                    !string.IsNullOrWhiteSpace(configurationFile) ? Path.GetDirectoryName(configurationFile)! : Environment.CurrentDirectory,
                    configuration.Options.Paths.Root ?? string.Empty
                );
            }
            catch (FileNotFoundException)
            {
                app.Logger.LogError($"\"{configurationFile}\" not found");
            }
            catch (Exception ex)
            {
                app.Logger.LogError($"Error decoding \"{configurationFile}\": {ex.Message}");
            }

            if (configuration == null)
            {
                app.Logger.LogWarning("Using default configuration and MapLibre Demo Tiles");

                configuration = new Configuration()
                {
                    Options = new Configuration.ConfigurationOptions()
                    {
                        Paths = new Configuration.ConfigurationOptions.ConfigurationOptionsPaths()
                    },
                    Styles = new Dictionary<string, Configuration.ConfigurationStyle>()
                {
                    {
                        "demotiles",
                        new Configuration.ConfigurationStyle()
                        {
                            Style = "https://raw.githubusercontent.com/maplibre/demotiles/gh-pages/style.json"
                        }
                    }
                },
                    Data = new Dictionary<string, Configuration.ConfigurationData>()
                };
            }

            Util.TileSize = configuration.Options!.TileSize;

            await Server.Init(configuration, app, app, builder.Environment);

            if (builder.Configuration.GetValue<bool?>("UseOutputCache") ?? false)
            {
                app.UseOutputCache();
            }

            app.Run();
        }
    }
}
