using System;
using System.IO;
using System.Text.Json;
using CameraCalibrationStudio.Models;

namespace CameraCalibrationStudio.Services
{
    /// <summary>
    /// Loads/saves calibration & color profiles as JSON under %AppData%\CameraCalibrationStudio.
    /// Also exposes the RTSP Camera Viewer's saved camera list (read-only) for convenience.
    /// </summary>
    public static class ProfileStore
    {
        private static readonly string StoreDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CameraCalibrationStudio");

        private static readonly string CalibrationFile = Path.Combine(StoreDir, "calibration.json");
        private static readonly string ColorFile = Path.Combine(StoreDir, "color.json");

        private static readonly string RtspViewerCamerasFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RtspCameraViewer", "cameras.json");

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static CalibrationProfile? LoadCalibration()
        {
            try
            {
                if (!File.Exists(CalibrationFile)) return null;
                return JsonSerializer.Deserialize<CalibrationProfile>(File.ReadAllText(CalibrationFile), JsonOptions);
            }
            catch { return null; }
        }

        public static void SaveCalibration(CalibrationProfile profile)
        {
            Directory.CreateDirectory(StoreDir);
            File.WriteAllText(CalibrationFile, JsonSerializer.Serialize(profile, JsonOptions));
        }

        public static ColorProfile? LoadColor()
        {
            try
            {
                if (!File.Exists(ColorFile)) return null;
                return JsonSerializer.Deserialize<ColorProfile>(File.ReadAllText(ColorFile), JsonOptions);
            }
            catch { return null; }
        }

        public static void SaveColor(ColorProfile profile)
        {
            Directory.CreateDirectory(StoreDir);
            File.WriteAllText(ColorFile, JsonSerializer.Serialize(profile, JsonOptions));
        }

        /// <summary>
        /// One camera as the RTSP Camera Viewer stores it. <paramref name="Store"/> is the group
        /// it was filed under there; <paramref name="Order"/> is the position it was arranged in.
        /// </summary>
        public sealed record SavedCamera(string Name, string Url, string Store, int Order)
        {
            /// <summary>Group header to show. A camera with no store still has to appear
            /// somewhere — losing it because a field is blank would be worse than a catch-all.</summary>
            public string GroupName => string.IsNullOrWhiteSpace(Store) ? "Ungrouped" : Store;
        }

        /// <summary>
        /// Cameras from the RTSP Camera Viewer's saved list, grouped as they are there.
        ///
        /// Returned in the viewer's own arrangement — stores in the order their first camera
        /// appears, cameras within a store by their own order — rather than alphabetically, so
        /// the list reads the same in both apps. That grouping is not decoration: names repeat
        /// across stores (there is an "Everlite" in two of them), so a flat list genuinely
        /// cannot tell them apart.
        /// </summary>
        public static System.Collections.Generic.List<SavedCamera> LoadRtspViewerCameras()
        {
            var cameras = new System.Collections.Generic.List<SavedCamera>();
            try
            {
                if (!File.Exists(RtspViewerCamerasFile)) return cameras;

                using var doc = JsonDocument.Parse(File.ReadAllText(RtspViewerCamerasFile));
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    var url = el.TryGetProperty("Url", out var u) ? u.GetString() ?? "" : "";
                    var store = el.TryGetProperty("Store", out var s) ? s.GetString() ?? "" : "";
                    var order = el.TryGetProperty("Order", out var o) && o.TryGetInt32(out var parsed)
                        ? parsed
                        : int.MaxValue;

                    if (!string.IsNullOrWhiteSpace(url))
                        cameras.Add(new SavedCamera(name, url, store, order));
                }
            }
            catch { /* best-effort — a malformed file means no saved cameras, not an error */ }

            // Sorted here rather than in each consumer: WPF grouping follows the order items
            // appear in, so getting it right once gives every picker the viewer's arrangement.
            var storeRank = cameras
                .GroupBy(c => c.GroupName)
                .ToDictionary(g => g.Key, g => g.Min(c => c.Order));

            return cameras
                .OrderBy(c => storeRank[c.GroupName])
                .ThenBy(c => c.Order)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Adds (or updates, by matching URL) a camera in the same saved-camera list the RTSP
        /// Camera Viewer app reads from — so a camera typed once in "Grab Frame from RTSP" shows
        /// up in the saved list next time, in either app. Returns false if name/url are invalid.
        /// </summary>
        public static bool SaveRtspViewerCamera(string name, string url)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) return false;

            System.Text.Json.Nodes.JsonArray root;
            try
            {
                root = File.Exists(RtspViewerCamerasFile)
                    ? (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(RtspViewerCamerasFile)) as System.Text.Json.Nodes.JsonArray)
                      ?? new System.Text.Json.Nodes.JsonArray()
                    : new System.Text.Json.Nodes.JsonArray();
            }
            catch
            {
                root = new System.Text.Json.Nodes.JsonArray();
            }

            var existing = root.FirstOrDefault(n => string.Equals((string?)n?["Url"], url, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing["Name"] = name;
            }
            else
            {
                root.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["Id"] = Guid.NewGuid().ToString("N"),
                    ["Name"] = name,
                    ["Url"] = url,
                    ["Username"] = null,
                    ["Password"] = null
                });
            }

            var dir = Path.GetDirectoryName(RtspViewerCamerasFile)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(RtspViewerCamerasFile, root.ToJsonString(JsonOptions));
            return true;
        }
    }
}
