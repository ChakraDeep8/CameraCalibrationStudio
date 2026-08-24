using System.Collections.ObjectModel;

namespace CameraCalibrationStudio.Models.Roi
{
    using CameraCalibrationStudio.Models;

    /// <summary>
    /// The single authoritative in-memory calibration state: image metadata, visibility
    /// adjustments (preview-only, never affect geometry) and the drawn calibration objects.
    /// Canvas, object list and JSON preview all render from this one model.
    /// </summary>
    public class RoiDocument
    {
        public string ImageFileName { get; set; } = "";
        public string ImagePath { get; set; } = "";
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }

        // Optional production metadata (used by the production zones export adapter).
        public string DeviceId { get; set; } = "";
        public string StoreCode { get; set; } = "";
        public string AreaId { get; set; } = "";

        /// <summary>Preview-only brightness/contrast/sharpness/color — shared type with Image Editor.</summary>
        public AdjustmentSettings Adjustments { get; set; } = new();

        /// <summary>Whether the "adjustments" block is written into the exported/live calibration JSON.</summary>
        public bool IncludeAdjustmentsInJson { get; set; } = true;
        public ObservableCollection<CalibrationObjectBase> Objects { get; } = new();

        public bool HasImage => ImageWidth > 0 && ImageHeight > 0;
    }
}
