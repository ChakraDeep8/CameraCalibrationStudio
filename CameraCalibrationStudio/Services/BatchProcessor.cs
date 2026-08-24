using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CameraCalibrationStudio.Models;
using OpenCvSharp;

namespace CameraCalibrationStudio.Services
{
    public class BatchOptions
    {
        public bool ApplyUndistort { get; set; }
        public bool ApplyAdjustments { get; set; }
        public AdjustmentSettings Adjustments { get; set; } = new();
        public bool Resize { get; set; }
        public int ResizeWidth { get; set; } = 1024;
        public int ResizeHeight { get; set; } = 768;
    }

    /// <summary>
    /// Applies one calibrated camera's adjustments/undistortion to a batch of related images —
    /// launched from ROI Calibration's "Batch…" button, not a separate top-level page.
    /// </summary>
    public static class BatchProcessor
    {
        private static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

        public static IEnumerable<string> EnumerateImages(string folder) =>
            Directory.EnumerateFiles(folder)
                .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

        /// <summary>
        /// Processes every image in inputFolder and writes results to outputFolder (same filenames).
        /// Calls onProgress(fileName, index, total) after each file; returns count of failures.
        /// </summary>
        public static int Run(string inputFolder, string outputFolder, BatchOptions options,
            CalibrationProfile? calibration,
            Action<string, int, int> onProgress, Action<string, string> onError)
        {
            Directory.CreateDirectory(outputFolder);
            var files = EnumerateImages(inputFolder).ToList();
            int failures = 0;

            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                try
                {
                    using var src = Cv2.ImRead(file, ImreadModes.Color);
                    if (src.Empty()) throw new InvalidOperationException("Could not decode image.");

                    Mat current = src.Clone();
                    current = ApplyPipeline(current, options, calibration);

                    var outPath = Path.Combine(outputFolder, Path.GetFileName(file));
                    Cv2.ImWrite(outPath, current);
                    current.Dispose();
                }
                catch (Exception ex)
                {
                    failures++;
                    onError(file, ex.Message);
                }

                onProgress(Path.GetFileName(file), i + 1, files.Count);
            }

            return failures;
        }

        private static Mat ApplyPipeline(Mat input, BatchOptions o, CalibrationProfile? calibration)
        {
            Mat current = input;

            void Step(Func<Mat, Mat> op)
            {
                var next = op(current);
                if (!ReferenceEquals(next, current)) current.Dispose();
                current = next;
            }

            if (o.ApplyUndistort && calibration != null)
                Step(m => ImageOpsService.Undistort(m, calibration.CameraMatrix, calibration.DistCoeffs));

            if (o.ApplyAdjustments && !o.Adjustments.IsDefault)
                Step(m => ImageOpsService.ApplyAdjustments(m, o.Adjustments));

            if (o.Resize)
                Step(m => ImageOpsService.Resize(m, o.ResizeWidth, o.ResizeHeight));

            return current;
        }
    }
}
