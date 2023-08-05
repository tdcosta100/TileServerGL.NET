using System.Security.Principal;
using System.Text.Json.Nodes;

namespace TileServerGL
{
    public class Configuration
    {
        #region Classes
        public class ConfigurationOptions
        {
            #region Classes
            public class ConfigurationOptionsPaths
            {
                public string Root { get; set; } = string.Empty;
                public string Fonts { get; set; } = string.Empty;
                public string Sprites { get; set; } = string.Empty;
                public string Icons { get; set; } = string.Empty;
                public string Styles { get; set; } = string.Empty;
                public string MBTiles { get; set; } = string.Empty;
            }
            #endregion

            #region Properties
            public ConfigurationOptionsPaths? Paths { get; set; }
            public string[] Domains { get; set; } = new string[0];
            public object FrontPage { get; set; } = true;
            public Dictionary<string, int> FormatQuality { get; set; } = new Dictionary<string, int>() { { "png", 100 }, { "jpeg", 100 }, { "webp", 100 } };
            public int MaxScaleFactor { get; set; } = 3;
            public int MaxSize { get; set; } = 2048;
            public int TileSize { get; set; } = 256;
            public int TileMargin { get; set; } = 0;
            public int[] MinRendererPoolSizes { get; set; } = new[] { 8, 4, 2 };
            public int[] MaxRendererPoolSizes { get; set; } = new[] { 16, 8, 4 };
            public bool ServeAllStyles { get; set; } = false;
            public bool ServeAllFonts { get; set; } = false;
            public bool ServeStaticMaps { get; set; } = true;
            public double[] ServeBounds { get; set; } = new[] { -180.0, -85.051128779807, 180.0, 85.051128779807 };
            public string Watermark { get; set; } = string.Empty;
            public bool AllowRemoteMarkerIcons { get; set; } = false;
            public DateTime CacheDate { get; set; } = DateTime.MaxValue;
            #endregion
        }

        public class ConfigurationStyle
        {
            public string Style { get; set; } = string.Empty;
            public string SpritePath { get; set; } = string.Empty;
            public JsonNode? StyleJSON { get; set; } = null;
            public bool ServeRendered { get; set; } = true;
            public bool ServeData { get; set; } = true;
            public JsonNode? TileJSON { get; set; } = null;
        }

        public class ConfigurationData
        {
            public string MBTiles { get; set; } = string.Empty;
            public JsonNode? TileJSON { get; set; } = null;
        }
        #endregion

        #region Properties
        public ConfigurationOptions? Options { get; set; }
        public Dictionary<string, ConfigurationStyle> Styles { get; set; } = new Dictionary<string, ConfigurationStyle>();
        public Dictionary<string, ConfigurationData> Data { get; set; } = new Dictionary<string, ConfigurationData>();
        #endregion
    }
}
