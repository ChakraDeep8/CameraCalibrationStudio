using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = OpenCvSharp.Size;

namespace CameraCalibrationStudio.Services
{
    /// <summary>
    /// Turns a detector's bounding box into points that follow the object's actual silhouette.
    /// A detection box says "the thing is somewhere in here"; a calibration region wants to
    /// trace the thing itself, so each box is handed to GrabCut — whose API is seeded with
    /// exactly such a rectangle — and the resulting foreground mask is traced and simplified
    /// into a handful of draggable vertices.
    ///
    /// Refinement is best-effort by design: GrabCut genuinely struggles on low-contrast IR
    /// crops, and a confidently wrong polygon is worse for the technician than an honest
    /// rectangle. Every failure path returns null so the caller keeps the original box.
    /// </summary>
    public static class OutlineRefiner
    {
        /// <summary>Longest side GrabCut actually runs on. Full-resolution crops are far slower
        /// with no benefit at calibration precision — the polygon is scaled back up afterwards.</summary>
        private const int MaxWorkingSize = 320;

        /// <summary>Proportion of the box padded in on each side to give GrabCut some guaranteed
        /// background to model. Seeded with a rect that fills its whole crop it has no background
        /// to learn from and degenerates.</summary>
        private const double CropPadding = 0.15;

        /// <summary>
        /// Traces the object inside <paramref name="box"/> and returns its outline as points in
        /// ORIGINAL frame coordinates, or null when the result fails a sanity check and the
        /// caller should keep the plain rectangle.
        /// </summary>
        public static List<Point>? RefineToOutline(Mat source, Rect box, int maxVertices = 14)
        {
            try
            {
                if (source.Channels() < 3) return null; // GrabCut needs colour to model fg/bg
                if (box.Width < 12 || box.Height < 12) return null;

                // --- crop a padded region around the box -------------------------------
                int padX = (int)Math.Round(box.Width * CropPadding);
                int padY = (int)Math.Round(box.Height * CropPadding);

                int cropX = (int)Math.Max(0, Math.Round(box.X) - padX);
                int cropY = (int)Math.Max(0, Math.Round(box.Y) - padY);
                int cropRight = (int)Math.Min(source.Width, Math.Round(box.X + box.Width) + padX);
                int cropBottom = (int)Math.Min(source.Height, Math.Round(box.Y + box.Height) + padY);
                int cropW = cropRight - cropX, cropH = cropBottom - cropY;
                if (cropW < 16 || cropH < 16) return null;

                using var crop = new Mat(source, new OpenCvSharp.Rect(cropX, cropY, cropW, cropH));

                // --- downscale for speed ------------------------------------------------
                double scale = Math.Min(1.0, (double)MaxWorkingSize / Math.Max(cropW, cropH));
                int workW = Math.Max(16, (int)Math.Round(cropW * scale));
                int workH = Math.Max(16, (int)Math.Round(cropH * scale));

                using var work = new Mat();
                Cv2.Resize(crop, work, new Size(workW, workH), interpolation: InterpolationFlags.Area);

                // The box, expressed inside the downscaled crop, is GrabCut's foreground seed.
                var seed = new OpenCvSharp.Rect(
                    (int)Math.Round((box.X - cropX) * scale),
                    (int)Math.Round((box.Y - cropY) * scale),
                    (int)Math.Round(box.Width * scale),
                    (int)Math.Round(box.Height * scale));
                seed = ClampRect(seed, workW, workH);
                if (seed.Width < 8 || seed.Height < 8) return null;

                // --- segment -------------------------------------------------------------
                using var mask = new Mat();
                using var bgdModel = new Mat();
                using var fgdModel = new Mat();
                Cv2.GrabCut(work, mask, seed, bgdModel, fgdModel, 3, GrabCutModes.InitWithRect);

                // GrabCut labels 0/2 background and 1/3 foreground, so the low bit is the answer.
                using var lowBit = new Mat();
                using var ones = new Mat(mask.Size(), MatType.CV_8UC1, new Scalar(1));
                Cv2.BitwiseAnd(mask, ones, lowBit);
                using var binary = new Mat();
                Cv2.Threshold(lowBit, binary, 0, 255, ThresholdTypes.Binary);

                // Close small holes so a textured object doesn't trace as lace.
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
                Cv2.MorphologyEx(binary, binary, MorphTypes.Close, kernel);

                Cv2.FindContours(binary, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                if (contours.Length == 0) return null;

                var largest = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
                double area = Cv2.ContourArea(largest);
                double seedArea = (double)seed.Width * seed.Height;
                if (seedArea <= 0) return null;

                // --- sanity gate ----------------------------------------------------------
                // A mask that covers almost none of the box means GrabCut found nothing; one that
                // covers almost all of it means it simply returned the box back and the polygon
                // adds nothing. Both cases are better served by the plain rectangle.
                double coverage = area / seedArea;
                if (coverage < 0.15 || coverage > 0.95) return null;

                var simplified = SimplifyToVertexBudget(largest, maxVertices);
                if (simplified.Length < 3) return null;

                // --- back to original frame coordinates -----------------------------------
                return simplified
                    .Select(p => new Point(cropX + p.X / scale, cropY + p.Y / scale))
                    .ToList();
            }
            catch
            {
                return null; // refinement is an enhancement, never a hard dependency
            }
        }

        private static OpenCvSharp.Rect ClampRect(OpenCvSharp.Rect r, int width, int height)
        {
            int x = Math.Clamp(r.X, 0, Math.Max(0, width - 1));
            int y = Math.Clamp(r.Y, 0, Math.Max(0, height - 1));
            int w = Math.Clamp(r.Width, 0, width - x);
            int h = Math.Clamp(r.Height, 0, height - y);
            return new OpenCvSharp.Rect(x, y, w, h);
        }

        /// <summary>Re-runs ApproxPolyDP with a growing epsilon until the simplified outline fits
        /// within maxVertices points — enough to still hug a real silhouette while staying a sane
        /// number of draggable handles once it lands on the canvas as a PolygonObject.</summary>
        internal static OpenCvSharp.Point[] SimplifyToVertexBudget(OpenCvSharp.Point[] contour, int maxVertices)
        {
            double peri = Cv2.ArcLength(contour, true);
            double epsilonFactor = 0.006;
            var approx = Cv2.ApproxPolyDP(contour, epsilonFactor * peri, true);

            int guard = 0;
            while (approx.Length > maxVertices && guard++ < 12)
            {
                epsilonFactor *= 1.4;
                approx = Cv2.ApproxPolyDP(contour, epsilonFactor * peri, true);
            }
            return approx;
        }
    }
}
