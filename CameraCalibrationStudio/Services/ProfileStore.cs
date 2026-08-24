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

        /// <summary>Name/Url pairs from the RTSP Camera Viewer app's saved list, if present.</summary>
        public static System.Collections.Generic.List<(string Name, string Url)> LoadRtspViewerCameras()
        {
            var result = new System.Collections.Generic.List<(string, string)>();
            try
            {
                if (!File.Exists(RtspViewerCamerasFile)) return result;
                using var doc = JsonDocument.Parse(File.ReadAllText(RtspViewerCamerasFile));
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    var url = el.TryGetProperty("Url", out var u) ? u.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(url))
                        result.Add((name, url));
                }
            }
            catch { /* best-effort */ }
            return result;
        }
    }
}
