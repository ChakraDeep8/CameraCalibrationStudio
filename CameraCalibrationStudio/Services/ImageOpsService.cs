using System;
using System.Collections.Generic;
using CameraCalibrationStudio.Models;
using OpenCvSharp;

namespace CameraCalibrationStudio.Services
{
    /// <summary>
    /// Pure, non-destructive image operations. Every method returns a new Mat;
    /// the caller owns disposal of both input and output.
    /// </summary>
    public static class ImageOpsService
    {
        /// <summary>
        /// The single, shared preview pipeline used by both ROI Calibration and Image Editor:
        /// brightness → contrast → sharpness → color calibration → optional exclusive filter.
        /// Always reconstructed from the given source (never from a previously-adjusted frame),
        /// so nothing accumulates regardless of how many times settings change.
        /// </summary>
        public static Mat ApplyAdjustments(Mat src, AdjustmentSettings s)
        {
            double alpha = 1.0 + s.Contrast / 100.0;
            Mat current = new Mat();
            src.ConvertTo(current, -1, alpha, s.Brightness);

            double sharpAmount = (s.Sharpness - 100.0) / 100.0;
            if (sharpAmount > 0.01)
            {
                using var blurred = new Mat();
                Cv2.GaussianBlur(current, blurred, new Size(0, 0), 3);
                var sharpened = new Mat();
                Cv2.AddWeighted(current, 1 + sharpAmount, blurred, -sharpAmount, 0, sharpened);
                current.Dispose();
                current = sharpened;
            }
            else if (sharpAmount < -0.01)
            {
                var blurred = new Mat();
                Cv2.GaussianBlur(current, blurred, new Size(0, 0), -sharpAmount * 4 + 0.1);
                current.Dispose();
                current = blurred;
            }

            if (s.AutoWhiteBalance)
            {
                var wb = AutoWhiteBalance(current);
                current.Dispose();
                current = wb;
            }
            if (Math.Abs(s.Temperature) > 0.5)
            {
                var t = Temperature(current, s.Temperature);
                current.Dispose();
                current = t;
            }
            if (Math.Abs(s.Saturation) > 0.5)
            {
                var sat = Saturation(current, s.Saturation);
                current.Dispose();
                current = sat;
            }
            if (Math.Abs(s.Exposure) > 0.5)
            {
                var ex = Exposure(current, s.Exposure);
                current.Dispose();
                current = ex;
            }

            if (!string.IsNullOrEmpty(s.FilterName) && s.FilterName != "None" && NamedFilters.TryGetValue(s.FilterName, out var filterFunc))
            {
                var filtered = filterFunc(current);
                current.Dispose();
                current = filtered;
            }

            return current;
        }

        /// <summary>The full, exclusive style-filter set — shared by Image Editor and ROI Calibration's filter gallery.</summary>
        public static readonly IReadOnlyDictionary<string, Func<Mat, Mat>> NamedFilters = new Dictionary<string, Func<Mat, Mat>>
        {
            ["Grayscale"] = Grayscale,
            ["Invert"] = Invert,
            ["Blur"] = m => GaussianBlur(m),
            ["Denoise"] = Denoise,
            ["Edges"] = m => EdgeDetect(m),
            ["Black && White"] = m => BlackAndWhite(m),
            ["Sepia"] = Sepia,
            ["Vintage"] = Vintage,
            ["Warm"] = WarmTone,
            ["Cool"] = CoolTone,
            ["Vignette"] = Vignette,
            ["Posterize"] = m => Posterize(m),
            ["Solarize"] = m => Solarize(m),
            ["Emboss"] = Emboss,
            ["Pencil Sketch"] = PencilSketch,
            ["Cartoon"] = Cartoon,
            ["Sharpen"] = Sharpen,
        };

        public static Mat RotateClockwise90(Mat src)
        {
            var dst = new Mat();
            Cv2.Rotate(src, dst, RotateFlags.Rotate90Clockwise);
            return dst;
        }

        public static Mat RotateCounterClockwise90(Mat src)
        {
            var dst = new Mat();
            Cv2.Rotate(src, dst, RotateFlags.Rotate90Counterclockwise);
            return dst;
        }

        public static Mat Rotate180(Mat src)
        {
            var dst = new Mat();
            Cv2.Rotate(src, dst, RotateFlags.Rotate180);
            return dst;
        }

        public static Mat FlipHorizontal(Mat src)
        {
            var dst = new Mat();
            Cv2.Flip(src, dst, FlipMode.Y);
            return dst;
        }

        public static Mat FlipVertical(Mat src)
        {
            var dst = new Mat();
            Cv2.Flip(src, dst, FlipMode.X);
            return dst;
        }

        public static Mat Crop(Mat src, Rect roi)
        {
            var safeRoi = roi.Intersect(new Rect(0, 0, src.Width, src.Height));
            if (safeRoi.Width <= 0 || safeRoi.Height <= 0)
                throw new ArgumentException("Crop rectangle is outside the image bounds.");
            return new Mat(src, safeRoi).Clone();
        }

        public static Mat Resize(Mat src, int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException("Width/height must be positive.");
            var dst = new Mat();
            Cv2.Resize(src, dst, new Size(width, height), interpolation: InterpolationFlags.Lanczos4);
            return dst;
        }

        /// <summary>alpha = contrast (1.0 = unchanged), beta = brightness offset (-100..100 typical).</summary>
        public static Mat BrightnessContrast(Mat src, double alpha, double beta)
        {
            var dst = new Mat();
            src.ConvertTo(dst, -1, alpha, beta);
            return dst;
        }

        /// <summary>Inverts (negates) every pixel value: result = 255 - pixel.</summary>
        public static Mat Invert(Mat src)
        {
            var dst = new Mat();
            Cv2.BitwiseNot(src, dst);
            return dst;
        }

        public static Mat Grayscale(Mat src)
        {
            var dst = new Mat();
            if (src.Channels() == 1)
            {
                src.CopyTo(dst);
                return dst;
            }
            Cv2.CvtColor(src, dst, ColorConversionCodes.BGR2GRAY);
            Cv2.CvtColor(dst, dst, ColorConversionCodes.GRAY2BGR); // keep 3-channel for consistent display/save
            return dst;
        }

        public static Mat Sharpen(Mat src)
        {
            using var blurred = new Mat();
            Cv2.GaussianBlur(src, blurred, new Size(0, 0), 3);
            var dst = new Mat();
            Cv2.AddWeighted(src, 1.5, blurred, -0.5, 0, dst);
            return dst;
        }

        public static Mat Denoise(Mat src)
        {
            var dst = new Mat();
            if (src.Channels() == 3)
                Cv2.FastNlMeansDenoisingColored(src, dst, 7, 7, 7, 21);
            else
                Cv2.FastNlMeansDenoising(src, dst, 7, 7, 21);
            return dst;
        }

        public static Mat EdgeDetect(Mat src, double threshold1 = 80, double threshold2 = 160)
        {
            using var gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            using var edges = new Mat();
            Cv2.Canny(gray, edges, threshold1, threshold2);
            var dst = new Mat();
            Cv2.CvtColor(edges, dst, ColorConversionCodes.GRAY2BGR);
            return dst;
        }

        public static Mat HistogramEqualize(Mat src)
        {
            if (src.Channels() == 1)
            {
                var g = new Mat();
                Cv2.EqualizeHist(src, g);
                return g;
            }

            // Equalize the luminance channel only, to avoid color shifts.
            using var ycrcb = new Mat();
            Cv2.CvtColor(src, ycrcb, ColorConversionCodes.BGR2YCrCb);
            Mat[] channels = Cv2.Split(ycrcb);
            try
            {
                Cv2.EqualizeHist(channels[0], channels[0]);
                using var merged = new Mat();
                Cv2.Merge(channels, merged);
                var dst = new Mat();
                Cv2.CvtColor(merged, dst, ColorConversionCodes.YCrCb2BGR);
                return dst;
            }
            finally
            {
                foreach (var c in channels) c.Dispose();
            }
        }

        public static Mat GaussianBlur(Mat src, double sigma = 4)
        {
            var dst = new Mat();
            Cv2.GaussianBlur(src, dst, new Size(0, 0), sigma);
            return dst;
        }

        public static Mat Sepia(Mat src)
        {
            using var kernel = Mat.FromArray(new float[,]
            {
                { 0.131f, 0.534f, 0.272f },
                { 0.168f, 0.686f, 0.349f },
                { 0.189f, 0.769f, 0.393f },
            });
            var dst = new Mat();
            Cv2.Transform(src, dst, kernel);
            return dst;
        }

        public static Mat BlackAndWhite(Mat src, double threshold = 128)
        {
            using var gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            using var bw = new Mat();
            Cv2.Threshold(gray, bw, threshold, 255, ThresholdTypes.Binary);
            var dst = new Mat();
            Cv2.CvtColor(bw, dst, ColorConversionCodes.GRAY2BGR);
            return dst;
        }

        public static Mat Posterize(Mat src, int levels = 4)
        {
            levels = Math.Clamp(levels, 2, 32);
            double step = 255.0 / (levels - 1);
            using var lut = new Mat(1, 256, MatType.CV_8U);
            for (int i = 0; i < 256; i++)
                lut.Set(0, i, (byte)Math.Round(Math.Round(i / step) * step));
            var dst = new Mat();
            Cv2.LUT(src, lut, dst);
            return dst;
        }

        public static Mat Solarize(Mat src, double threshold = 128)
        {
            using var lut = new Mat(1, 256, MatType.CV_8U);
            for (int i = 0; i < 256; i++)
                lut.Set(0, i, (byte)(i < threshold ? i : 255 - i));
            var dst = new Mat();
            Cv2.LUT(src, lut, dst);
            return dst;
        }

        public static Mat Emboss(Mat src)
        {
            using var kernel = Mat.FromArray(new float[,]
            {
                { -2, -1, 0 },
                { -1,  1, 1 },
                {  0,  1, 2 },
            });
            using var gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            using var embossed = new Mat();
            Cv2.Filter2D(gray, embossed, -1, kernel, delta: 128);
            var dst = new Mat();
            Cv2.CvtColor(embossed, dst, ColorConversionCodes.GRAY2BGR);
            return dst;
        }

        public static Mat WarmTone(Mat src)
        {
            Mat[] ch = Cv2.Split(src);
            try
            {
                ch[2].ConvertTo(ch[2], -1, 1, 18);  // R up
                ch[0].ConvertTo(ch[0], -1, 1, -12); // B down
                var dst = new Mat();
                Cv2.Merge(ch, dst);
                return dst;
            }
            finally { foreach (var c in ch) c.Dispose(); }
        }

        public static Mat CoolTone(Mat src)
        {
            Mat[] ch = Cv2.Split(src);
            try
            {
                ch[0].ConvertTo(ch[0], -1, 1, 18);  // B up
                ch[2].ConvertTo(ch[2], -1, 1, -12); // R down
                var dst = new Mat();
                Cv2.Merge(ch, dst);
                return dst;
            }
            finally { foreach (var c in ch) c.Dispose(); }
        }

        public static Mat Vignette(Mat src)
        {
            int w = src.Width, h = src.Height;
            using var kernelX = Cv2.GetGaussianKernel(w, w / 2.2);
            using var kernelY = Cv2.GetGaussianKernel(h, h / 2.2);
            using var kernel = kernelY! * kernelX!.T();
            using var mask32 = new Mat();
            Cv2.Normalize(kernel, mask32, 0, 1, NormTypes.MinMax);
            using var mask8 = new Mat();
            mask32.ConvertTo(mask8, MatType.CV_8U, 255.0);

            Mat[] ch = Cv2.Split(src);
            try
            {
                foreach (var c in ch)
                {
                    using var scaled = new Mat();
                    Cv2.Multiply(c, mask8, scaled, 1.0 / 255.0);
                    scaled.CopyTo(c);
                }
                var dst = new Mat();
                Cv2.Merge(ch, dst);
                return dst;
            }
            finally { foreach (var c in ch) c.Dispose(); }
        }

        public static Mat PencilSketch(Mat src)
        {
            using var gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            using var inverted = new Mat();
            Cv2.BitwiseNot(gray, inverted);
            using var blurred = new Mat();
            Cv2.GaussianBlur(inverted, blurred, new Size(0, 0), 21);
            using var invertedBlur = new Mat();
            Cv2.BitwiseNot(blurred, invertedBlur);
            using var sketch = new Mat();
            Cv2.Divide(gray, invertedBlur, sketch, 256.0);
            var dst = new Mat();
            Cv2.CvtColor(sketch, dst, ColorConversionCodes.GRAY2BGR);
            return dst;
        }

        public static Mat Cartoon(Mat src)
        {
            using var gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            using var blurredGray = new Mat();
            Cv2.MedianBlur(gray, blurredGray, 5);
            using var edges = new Mat();
            Cv2.AdaptiveThreshold(blurredGray, edges, 255, AdaptiveThresholdTypes.MeanC, ThresholdTypes.Binary, 9, 8);

            using var smooth = new Mat();
            Cv2.BilateralFilter(src, smooth, 9, 200, 200);

            using var edgesColor = new Mat();
            Cv2.CvtColor(edges, edgesColor, ColorConversionCodes.GRAY2BGR);
            var dst = new Mat();
            Cv2.BitwiseAnd(smooth, edgesColor, dst);
            return dst;
        }

        public static Mat Vintage(Mat src)
        {
            using var sepia = Sepia(src);
            using var vignetted = Vignette(sepia);
            var dst = new Mat();
            vignetted.ConvertTo(dst, -1, 0.95, -8);
            return dst;
        }

        /// <summary>Shifts color temperature: positive = warmer (more red/less blue), negative = cooler.</summary>
        public static Mat Temperature(Mat src, double amount)
        {
            double gain = amount / 100.0 * 40; // -40..40 additive shift
            Mat[] ch = Cv2.Split(src);
            try
            {
                ch[2].ConvertTo(ch[2], -1, 1, gain);  // R
                ch[0].ConvertTo(ch[0], -1, 1, -gain); // B
                var dst = new Mat();
                Cv2.Merge(ch, dst);
                return dst;
            }
            finally { foreach (var c in ch) c.Dispose(); }
        }

        /// <summary>amount -100..100 maps to a 0..2 saturation multiplier (0 = grayscale, 100 = unchanged, 200 = vivid).</summary>
        public static Mat Saturation(Mat src, double amount)
        {
            double factor = 1.0 + amount / 100.0;
            using var hsv = new Mat();
            Cv2.CvtColor(src, hsv, ColorConversionCodes.BGR2HSV);
            Mat[] ch = Cv2.Split(hsv);
            try
            {
                ch[1].ConvertTo(ch[1], -1, factor, 0);
                using var merged = new Mat();
                Cv2.Merge(ch, merged);
                var dst = new Mat();
                Cv2.CvtColor(merged, dst, ColorConversionCodes.HSV2BGR);
                return dst;
            }
            finally { foreach (var c in ch) c.Dispose(); }
        }

        /// <summary>Multiplicative exposure gain, distinct from the additive Brightness control. amount -100..100.</summary>
        public static Mat Exposure(Mat src, double amount)
        {
            double gain = Math.Pow(2.0, amount / 100.0); // -100 => 0.5x, 0 => 1x, 100 => 2x
            var dst = new Mat();
            src.ConvertTo(dst, -1, gain, 0);
            return dst;
        }

        /// <summary>Automatic gray-world white balance: scales each channel so its mean matches the overall gray mean.</summary>
        public static Mat AutoWhiteBalance(Mat src)
        {
            Scalar mean = Cv2.Mean(src);
            double gray = (mean.Val0 + mean.Val1 + mean.Val2) / 3.0;
            if (gray < 1) gray = 1;

            Mat[] ch = Cv2.Split(src);
            try
            {
                double[] means = { mean.Val0, mean.Val1, mean.Val2 };
                for (int i = 0; i < 3; i++)
                {
                    double gain = means[i] > 1 ? gray / means[i] : 1.0;
                    gain = Math.Clamp(gain, 0.5, 2.0);
                    ch[i].ConvertTo(ch[i], -1, gain, 0);
                }
                var dst = new Mat();
                Cv2.Merge(ch, dst);
                return dst;
            }
            finally { foreach (var c in ch) c.Dispose(); }
        }

        /// <summary>Applies lens undistortion using a saved camera calibration profile.</summary>
        public static Mat Undistort(Mat src, double[] cameraMatrixRowMajor, double[] distCoeffs)
        {
            using var cameraMatrix = Mat.FromArray(To3x3(cameraMatrixRowMajor));
            using var dist = Mat.FromArray(distCoeffs);
            var dst = new Mat();
            Cv2.Undistort(src, dst, cameraMatrix, dist);
            return dst;
        }

        /// <summary>Applies per-channel gain (white balance) and an overall exposure gain.</summary>
        public static Mat ApplyColorProfile(Mat src, double gainB, double gainG, double gainR, double exposureGain)
        {
            Mat[] channels = Cv2.Split(src);
            try
            {
                channels[0].ConvertTo(channels[0], -1, gainB * exposureGain, 0);
                channels[1].ConvertTo(channels[1], -1, gainG * exposureGain, 0);
                channels[2].ConvertTo(channels[2], -1, gainR * exposureGain, 0);
                var dst = new Mat();
                Cv2.Merge(channels, dst);
                return dst;
            }
            finally
            {
                foreach (var c in channels) c.Dispose();
            }
        }

        private static double[,] To3x3(double[] rowMajor9)
        {
            if (rowMajor9.Length != 9) throw new ArgumentException("Camera matrix must have 9 elements.");
            return new double[,]
            {
                { rowMajor9[0], rowMajor9[1], rowMajor9[2] },
                { rowMajor9[3], rowMajor9[4], rowMajor9[5] },
                { rowMajor9[6], rowMajor9[7], rowMajor9[8] },
            };
        }
    }
}
