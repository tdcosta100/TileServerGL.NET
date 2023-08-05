using System.Reflection.Metadata;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TileServerGL
{
    public class Util
    {
        #region Properties
        public static int TileSize = 256;

        public const int InternalTileSize = 512;

        public const double DegreesToRadians = Math.PI / 180.0;

        public static Regex HttpRegex = new("^(https?:)?//");

        public static byte[] GZipSignature = new[] { (byte)0x1f, (byte)0x8b };
        #endregion

        #region Public methods
        public static string ResolvePath(params string[] paths)
        {
            ArgumentNullException.ThrowIfNull(paths);

            var pathsList = new List<string>(paths.Reverse());

            var finalPath = pathsList[0];
            pathsList.RemoveAt(0);

            while (pathsList.Count > 0 && !Path.IsPathRooted(finalPath))
            {
                finalPath = Path.Combine(pathsList[0], finalPath);

                if (Path.IsPathRooted(finalPath))
                {
                    finalPath = Path.GetFullPath(finalPath);
                }
                else
                {
                    finalPath = Path.GetRelativePath(Environment.CurrentDirectory, Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, finalPath)));
                }

                pathsList.RemoveAt(0);
            }

            if (!Path.IsPathRooted(finalPath))
            {
                finalPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, finalPath));
            }

            return finalPath;
        }

        public static string GetPublicUrl(HttpRequest request)
        {
            return $"{request.Scheme}://{request.Host}{request.PathBase}/";
        }

        public static void FixTileJSONCenter(JsonNode tileJSON)
        {
            if (tileJSON["bounds"] != null && tileJSON["center"] == null)
            {
                tileJSON["center"] = new JsonArray(
                    (tileJSON["bounds"]![0]!.GetValue<double>() + tileJSON["bounds"]![2]!.GetValue<double>()) / 2,
                    (tileJSON["bounds"]![1]!.GetValue<double>() + tileJSON["bounds"]![3]!.GetValue<double>()) / 2,
                    CalculateZoomForBoundingBox(
                        tileJSON["bounds"]![0]!.GetValue<double>(),
                        tileJSON["bounds"]![1]!.GetValue<double>(),
                        tileJSON["bounds"]![2]!.GetValue<double>(),
                        tileJSON["bounds"]![3]!.GetValue<double>(),
                        TileSize,
                        TileSize
                    )
                );
            }
        }

        public static double LongitudeToX(double longitude)
        {
            return (longitude + 180.0) / 360.0;
        }

        public static double LongitudeToX(double longitude, double zoom, int tileSize = InternalTileSize)
        {
            return LongitudeToX(longitude) * Math.Pow(2, zoom) * tileSize;
        }

        public static double LatitudeToY(double latitude)
        {
            return (1 - Math.Log(Math.Tan(latitude * DegreesToRadians) + 1 / Math.Cos(latitude * DegreesToRadians)) / Math.PI) / 2;
        }

        public static double LatitudeToY(double latitude, double zoom, int tileSize = InternalTileSize)
        {
            return LatitudeToY(latitude) * Math.Pow(2, zoom) * tileSize;
        }

        public static int LongitudeToTileX(double longitude, int zoom)
        {
            return (int)Math.Floor(LongitudeToX(longitude) * (1 << zoom));
        }

        public static int LatitudeToTileY(double latitude, int zoom)
        {
            return (int)Math.Floor(LatitudeToY(latitude) * (1 << zoom));
        }

        public static double CalculateZoomForBoundingBox(double lonMin, double latMin, double lonMax, double latMax, int pixelWidth, int pixelHeight, double padding = 0.1)
        {
            var minX = LongitudeToX(lonMin);
            var minY = LatitudeToY(latMax);
            var maxX = LongitudeToX(lonMax);
            var maxY = LatitudeToY(latMin);

            var boundsProportion = (maxX - minX)/(maxY - minY);
            var imageProportion = (double)pixelWidth / pixelHeight;

            (double boundingBoxSize, int imageSize) = (boundsProportion > imageProportion)
                ?
                (maxX - minX, pixelWidth)
                :
                (maxY - minY, pixelHeight);

            return Math.Max(Math.Log2(imageSize / (1 + 2 * padding) / boundingBoxSize / InternalTileSize), 0);
        }

        // Original code at https://gist.github.com/shinyzhu/4617989
        /// <summary>
        /// Decode google style polyline coordinates (LngLat)
        /// </summary>
        /// <param name="encodedPoints"></param>
        /// <returns></returns>
        public static IEnumerable<double> Decode(string encodedPoints)
        {
            ArgumentException.ThrowIfNullOrEmpty(encodedPoints, nameof(encodedPoints));

            var index = 0;
            var currentLat = 0;
            var currentLng = 0;
            int next5bits, sum, shifter;

            while (index < encodedPoints.Length)
            {
                // calculate next latitude
                sum = 0;
                shifter = 0;

                do
                {
                    next5bits = encodedPoints[index++] - 63;
                    sum |= (next5bits & 31) << shifter;
                    shifter += 5;
                } while (next5bits >= 32 && index < encodedPoints.Length);

                if (index >= encodedPoints.Length)
                {
                    break;
                }

                currentLat += (sum & 1) == 1 ? ~(sum >> 1) : (sum >> 1);

                //calculate next longitude
                sum = 0;
                shifter = 0;

                do
                {
                    next5bits = encodedPoints[index++] - 63;
                    sum |= (next5bits & 31) << shifter;
                    shifter += 5;
                } while (next5bits >= 32 && index < encodedPoints.Length);

                if (index >= encodedPoints.Length && next5bits >= 32)
                {
                    break;
                }

                currentLng += (sum & 1) == 1 ? ~(sum >> 1) : (sum >> 1);

                yield return currentLng / 1e5;
                yield return currentLat / 1e5;
            }
        }

        /// <summary>
        /// Decode google style polyline coordinates (LngLat)
        /// </summary>
        /// <param name="points"></param>
        /// <returns></returns>
        public static string Encode(IEnumerable<double> points)
        {
            var str = new StringBuilder();

            var encodeDiff = (int diff) =>
            {
                var shifted = diff << 1;

                if (diff < 0)
                {
                    shifted = ~shifted;
                }

                var rem = shifted;

                while (rem >= 0x20)
                {
                    str.Append((char)((0x20 | (rem & 0x1f)) + 63));
                    rem >>= 5;
                }

                str.Append((char)(rem + 63));
            };

            var lastLat = 0;
            var lastLng = 0;

            var index = 0;
            var pointsLength = points.Count();

            while (index + 1 < pointsLength)
            {
                var lng = (int)Math.Round(points.ElementAt(index) * 1e5);
                var lat = (int)Math.Round(points.ElementAt(index + 1) * 1e5);

                encodeDiff(lat - lastLat);
                encodeDiff(lng - lastLng);

                lastLng = lng;
                lastLat = lat;

                index += 2;
            }

            return str.ToString();
        }
        #endregion
    }
}
