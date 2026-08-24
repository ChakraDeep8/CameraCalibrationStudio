using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using CameraCalibrationStudio.Models.Roi;

namespace CameraCalibrationStudio.Services
{
    /// <summary>
    /// Serializes/deserializes the calibration document to/from JSON, and adapts it to a
    /// normalized-polygon "zones" schema some computer-vision pipelines expect
    /// (schema_version 1: device_id, area_id, frame_width/height, zones[] of
    /// {zone_id, kind, polygon:[[nx,ny],...]} with polygon coordinates normalized 0-1).
    /// Rectangles/squares are exported as their 4-corner polygon; this adapter is a best-effort
    /// mapping — review exported files against your actual consumer's schema before deploying.
    /// </summary>
    public static class RoiJsonService
    {
        private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

        // ---------------- Native (editor) schema ----------------

        public static JsonObject BuildNativeJson(RoiDocument doc)
        {
            var root = new JsonObject
            {
                ["version"] = 2,
                ["image"] = new JsonObject
                {
                    ["filename"] = doc.ImageFileName,
                    ["width"] = doc.ImageWidth,
                    ["height"] = doc.ImageHeight
                }
            };

            if (doc.IncludeAdjustmentsInJson)
            {
                root["adjustments"] = new JsonObject
                {
                    ["brightness"] = Math.Round(doc.Adjustments.Brightness, 2),
                    ["contrast"] = Math.Round(doc.Adjustments.Contrast, 2),
                    ["sharpness"] = Math.Round(doc.Adjustments.Sharpness, 2),
                    ["temperature"] = Math.Round(doc.Adjustments.Temperature, 2),
                    ["saturation"] = Math.Round(doc.Adjustments.Saturation, 2),
                    ["exposure"] = Math.Round(doc.Adjustments.Exposure, 2),
                    ["autoWhiteBalance"] = doc.Adjustments.AutoWhiteBalance
                };
            }

            root["objects"] = BuildObjectsArray(doc.Objects);
            return root;
        }

        private static JsonArray BuildObjectsArray(IEnumerable<CalibrationObjectBase> objects)
        {
            var arr = new JsonArray();
            foreach (var obj in objects)
            {
                JsonObject node = obj switch
                {
                    RectangleObject r => new JsonObject
                    {
                        ["name"] = r.Name,
                        ["type"] = r.IsSquare ? "square" : "rectangle",
                        ["x1"] = Math.Round(Math.Min(r.X1, r.X2), 1),
                        ["y1"] = Math.Round(Math.Min(r.Y1, r.Y2), 1),
                        ["x2"] = Math.Round(Math.Max(r.X1, r.X2), 1),
                        ["y2"] = Math.Round(Math.Max(r.Y1, r.Y2), 1)
                    },
                    PolygonObject p => new JsonObject
                    {
                        ["name"] = p.Name,
                        ["type"] = "polygon",
                        ["points"] = new JsonArray(p.Points.Select(pt =>
                            (JsonNode)new JsonArray(Math.Round(pt.X, 1), Math.Round(pt.Y, 1))).ToArray())
                    },
                    LineObject l => new JsonObject
                    {
                        ["name"] = l.Name,
                        ["type"] = "line",
                        ["start"] = new JsonArray(Math.Round(l.Start.X, 1), Math.Round(l.Start.Y, 1)),
                        ["end"] = new JsonArray(Math.Round(l.End.X, 1), Math.Round(l.End.Y, 1))
                    },
                    _ => new JsonObject()
                };
                arr.Add(node);
            }
            return arr;
        }

        public static string ToPrettyString(RoiDocument doc) => BuildNativeJson(doc).ToJsonString(PrettyOptions);

        public static void SaveNative(RoiDocument doc, string path) =>
            File.WriteAllText(path, ToPrettyString(doc));

        /// <summary>Loads a native-schema calibration file into an existing document (replacing its objects).</summary>
        public static void LoadNativeInto(RoiDocument doc, string path)
        {
            JsonNode root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path))
                       ?? throw new InvalidDataException("File is empty.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Invalid JSON: {ex.Message}");
            }

            var imageNode = root["image"];
            int width = (int?)imageNode?["width"] ?? 0;
            int height = (int?)imageNode?["height"] ?? 0;

            if (width > 0 && height > 0 && (width != doc.ImageWidth || height != doc.ImageHeight))
            {
                throw new CalibrationResolutionMismatchException(width, height, doc.ImageWidth, doc.ImageHeight);
            }

            doc.Objects.Clear();
            var adj = root["adjustments"];
            doc.IncludeAdjustmentsInJson = adj != null;
            if (adj != null)
            {
                doc.Adjustments.Brightness = (double?)adj["brightness"] ?? 0;
                doc.Adjustments.Contrast = (double?)adj["contrast"] ?? 0;
                doc.Adjustments.Sharpness = (double?)adj["sharpness"] ?? 0;
                doc.Adjustments.Temperature = (double?)adj["temperature"] ?? 0;
                doc.Adjustments.Saturation = (double?)adj["saturation"] ?? 0;
                doc.Adjustments.Exposure = (double?)adj["exposure"] ?? 0;
                doc.Adjustments.AutoWhiteBalance = (bool?)adj["autoWhiteBalance"] ?? false;
            }

            var objectsNode = root["objects"] as JsonArray;
            if (objectsNode == null) return;

            foreach (var node in objectsNode)
            {
                if (node == null) continue;
                var type = (string?)node["type"] ?? "";
                var name = (string?)node["name"] ?? "Unnamed";

                CalibrationObjectBase? shape = type switch
                {
                    "rectangle" or "square" => new RectangleObject
                    {
                        Name = name,
                        IsSquare = type == "square",
                        X1 = (double?)node["x1"] ?? 0,
                        Y1 = (double?)node["y1"] ?? 0,
                        X2 = (double?)node["x2"] ?? 0,
                        Y2 = (double?)node["y2"] ?? 0
                    },
                    "polygon" => new PolygonObject
                    {
                        Name = name,
                        Points = (node["points"] as JsonArray)?
                            .Select(p => new Point((double?)p![0] ?? 0, (double?)p[1] ?? 0))
                            .ToList() ?? new List<Point>()
                    },
                    "line" => new LineObject
                    {
                        Name = name,
                        Start = new Point((double?)node["start"]![0] ?? 0, (double?)node["start"]![1] ?? 0),
                        End = new Point((double?)node["end"]![0] ?? 0, (double?)node["end"]![1] ?? 0)
                    },
                    _ => null
                };

                if (shape != null) doc.Objects.Add(shape);
            }
        }

        // ---------------- Production "journey zones" export adapter ----------------

        public static JsonObject BuildProductionZonesJson(RoiDocument doc, string calibrationStatus = "calibrated")
        {
            var zones = new JsonArray();
            foreach (var obj in doc.Objects)
            {
                var polygon = ToNormalizedPolygon(obj, doc.ImageWidth, doc.ImageHeight);
                if (polygon == null) continue;

                zones.Add(new JsonObject
                {
                    ["zone_id"] = ToZoneId(obj.Name),
                    ["kind"] = obj.Kind == ShapeKind.Line ? "line" : "detection",
                    ["area_id"] = doc.AreaId,
                    ["polygon"] = polygon
                });
            }

            return new JsonObject
            {
                ["schema_version"] = 1,
                ["store_code"] = doc.StoreCode,
                ["device_id"] = doc.DeviceId,
                ["area_id"] = doc.AreaId,
                ["calibration_status"] = calibrationStatus,
                ["frame_width"] = doc.ImageWidth,
                ["frame_height"] = doc.ImageHeight,
                ["calibration_source"] = doc.ImageFileName,
                ["zones"] = zones
            };
        }

        public static void SaveProductionZones(RoiDocument doc, string path) =>
            File.WriteAllText(path, BuildProductionZonesJson(doc).ToJsonString(PrettyOptions));

        private static JsonArray? ToNormalizedPolygon(CalibrationObjectBase obj, int width, int height)
        {
            if (width <= 0 || height <= 0) return null;

            IEnumerable<Point> points = obj switch
            {
                RectangleObject r => new[]
                {
                    new Point(Math.Min(r.X1, r.X2), Math.Min(r.Y1, r.Y2)),
                    new Point(Math.Max(r.X1, r.X2), Math.Min(r.Y1, r.Y2)),
                    new Point(Math.Max(r.X1, r.X2), Math.Max(r.Y1, r.Y2)),
                    new Point(Math.Min(r.X1, r.X2), Math.Max(r.Y1, r.Y2)),
                },
                PolygonObject p => p.Points,
                LineObject l => new[] { l.Start, l.End },
                _ => Array.Empty<Point>()
            };

            var arr = new JsonArray();
            foreach (var pt in points)
                arr.Add(new JsonArray(Math.Round(pt.X / width, 6), Math.Round(pt.Y / height, 6)));
            return arr;
        }

        private static string ToZoneId(string name) =>
            name.Trim().ToLowerInvariant().Replace(' ', '_');

        // ---------------- Validation ----------------

        public static List<string> Validate(RoiDocument doc)
        {
            var errors = new List<string>();
            if (!doc.HasImage) errors.Add("No image is loaded.");

            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in doc.Objects)
            {
                if (string.IsNullOrWhiteSpace(obj.Name))
                    errors.Add("A region has no name.");
                else if (!seenNames.Add(obj.Name))
                    errors.Add($"The name \"{obj.Name}\" is used more than once.");

                switch (obj)
                {
                    case RectangleObject r when Math.Abs(r.X2 - r.X1) < 1 || Math.Abs(r.Y2 - r.Y1) < 1:
                        errors.Add($"\"{r.Name}\" has zero width or height.");
                        break;
                    case PolygonObject p when p.Points.Count < 3:
                        errors.Add($"\"{p.Name}\" contains only {p.Points.Count} point(s). A polygon requires at least three.");
                        break;
                    case LineObject l when l.Start == l.End:
                        errors.Add($"\"{l.Name}\" has identical start and end points.");
                        break;
                }
            }
            return errors;
        }
    }

    public class CalibrationResolutionMismatchException : Exception
    {
        public int FileWidth { get; }
        public int FileHeight { get; }
        public int ImageWidth { get; }
        public int ImageHeight { get; }

        public CalibrationResolutionMismatchException(int fileWidth, int fileHeight, int imageWidth, int imageHeight)
            : base($"This calibration file was created for a {fileWidth}x{fileHeight} image, but the loaded image is {imageWidth}x{imageHeight}.")
        {
            FileWidth = fileWidth; FileHeight = fileHeight; ImageWidth = imageWidth; ImageHeight = imageHeight;
        }
    }
}
