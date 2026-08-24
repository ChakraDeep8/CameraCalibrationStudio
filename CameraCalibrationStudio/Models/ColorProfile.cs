using System;

namespace CameraCalibrationStudio.Models
{
    /// <summary>
    /// Result of a simple gray-world white-balance / exposure calibration: per-channel
    /// gain factors that map a sampled reference patch to neutral gray, plus a brightness gain.
    /// </summary>
    public class ColorProfile
    {
        public string Name { get; set; } = "Color Calibration";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        public double GainB { get; set; } = 1.0;
        public double GainG { get; set; } = 1.0;
        public double GainR { get; set; } = 1.0;

        /// <summary>Overall exposure/brightness multiplier applied after white balance.</summary>
        public double ExposureGain { get; set; } = 1.0;

        /// <summary>Mean B,G,R sampled from the reference patch at calibration time (for reference/debug).</summary>
        public double SampledMeanB { get; set; }
        public double SampledMeanG { get; set; }
        public double SampledMeanR { get; set; }
    }
}
