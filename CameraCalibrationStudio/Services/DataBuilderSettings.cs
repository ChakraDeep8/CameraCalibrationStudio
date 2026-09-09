using System;
using System.IO;

namespace CameraCalibrationStudio.Services
{
    /// <summary>
    /// Remembers the Data Builder's output folder between sessions. Retyping a dataset path
    /// every launch is the kind of friction that makes people stop using a tool.
    /// </summary>
    public static class DataBuilderSettings
    {
        private static readonly string SettingsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CameraCalibrationStudio", "databuilder-output.txt");

        public static string LoadOutputFolder()
        {
            try { return File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile).Trim() : ""; }
            catch { return ""; }
        }

        public static void SaveOutputFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                File.WriteAllText(SettingsFile, folder ?? "");
            }
            catch { /* a settings write failing must never interrupt the workflow */ }
        }
    }
}
