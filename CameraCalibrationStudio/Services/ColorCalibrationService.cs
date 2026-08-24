using System;
using CameraCalibrationStudio.Models;
using OpenCvSharp;

namespace CameraCalibrationStudio.Services
{
    /// <summary>
    /// Simple reference-patch white-balance / exposure calibration: sample a region that
    /// should be neutral gray (or a known target), compute per-channel gains that bring it
    /// to neutral, and an overall exposure gain that brings its brightness to a target level.
    /// </summary>
    public static class ColorCalibrationService
    {
        public static ColorProfile CalibrateFromPatch(Mat image, Rect patchRoi, double targetGray = 200.0)
        {
            var safeRoi = patchRoi.Intersect(new Rect(0, 0, image.Width, image.Height));
            if (safeRoi.Width <= 0 || safeRoi.Height <= 0)
                throw new ArgumentException("Sample rectangle is outside the image bounds.");

            using var patch = new Mat(image, safeRoi);
            Scalar mean = Cv2.Mean(patch);
            double meanB = mean.Val0, meanG = mean.Val1, meanR = mean.Val2;

            double luminance = 0.114 * meanB + 0.587 * meanG + 0.299 * meanR;
            if (luminance < 1) luminance = 1;

            // Gains equalize B/G/R to each other (neutralize color cast), referenced to green.
            double gainB = meanB > 1 ? meanG / meanB : 1.0;
            double gainR = meanR > 1 ? meanG / meanR : 1.0;
            double gainG = 1.0;

            double exposureGain = targetGray / luminance;

            return new ColorProfile
            {
                GainB = Clamp(gainB),
                GainG = Clamp(gainG),
                GainR = Clamp(gainR),
                ExposureGain = Clamp(exposureGain, 0.2, 4.0),
                SampledMeanB = meanB,
                SampledMeanG = meanG,
                SampledMeanR = meanR
            };
        }

        private static double Clamp(double v, double min = 0.3, double max = 3.0) => Math.Clamp(v, min, max);
    }
}
