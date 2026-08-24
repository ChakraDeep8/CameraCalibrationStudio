namespace CameraCalibrationStudio.Models
{
    /// <summary>
    /// The complete set of non-destructive, preview-only adjustments — shared by the ROI
    /// Calibration workspace and the Image Editor so both pages apply brightness/contrast/
    /// sharpness/color identically instead of each re-implementing its own version.
    /// </summary>
    public class AdjustmentSettings
    {
        public double Brightness;          // -100..100
        public double Contrast;            // -100..100
        public double Sharpness = 100;     // 0..200 (100 = neutral)

        // Color calibration (ROI Calibration's collapsible "Color Calibration" section)
        public double Temperature;         // -100..100
        public double Saturation;          // -100..100 (0 = unchanged)
        public double Exposure;            // -100..100 (0 = unchanged)
        public bool AutoWhiteBalance;

        // Image Editor's exclusive style filter ("" or "None" = no filter)
        public string FilterName = "";

        public bool IsDefault =>
            Brightness == 0 && Contrast == 0 && Sharpness == 100 &&
            Temperature == 0 && Saturation == 0 && Exposure == 0 &&
            !AutoWhiteBalance && string.IsNullOrEmpty(FilterName);

        public AdjustmentSettings Clone() => new()
        {
            Brightness = Brightness, Contrast = Contrast, Sharpness = Sharpness,
            Temperature = Temperature, Saturation = Saturation, Exposure = Exposure,
            AutoWhiteBalance = AutoWhiteBalance, FilterName = FilterName
        };
    }
}
