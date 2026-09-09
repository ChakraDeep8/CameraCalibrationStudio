using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CameraCalibrationStudio.Models.Roi;
using OpenCvSharp;

namespace CameraCalibrationStudio.Services
{
    /// <summary>What happened to one region during an export.</summary>
    public sealed class CropResult
    {
        public string RegionName { get; init; } = "";
        public string ClassName { get; init; } = "";
        public string? SavedPath { get; init; }
        public string? Skipped { get; init; }
        public bool Saved => SavedPath != null;

        /// <summary>Frame this crop was taken from, and where in it — recorded so a batch can be
        /// written out as a manifest that ties every crop back to its source.</summary>
        public string SourceImage { get; init; } = "";
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public DateTime SavedUtc { get; init; }
    }

    /// <summary>
    /// Writes the drawn regions out as individual image files, one folder per class, building a
    /// classification dataset straight from the canvas:
    ///
    ///   &lt;output&gt;/Staff/&lt;source&gt;_01_143502.jpg
    ///   &lt;output&gt;/Customer/&lt;source&gt;_02_143502.jpg
    ///
    /// Crops come from the ORIGINAL full-resolution frame, so a dataset is never built from the
    /// capped preview or from a zoom-dependent rendering.
    /// </summary>
    public static class CropExportService
    {
        /// <summary>
        /// Crops every visible region and saves it under a folder named after its class.
        /// <paramref name="classNameResolver"/> maps a region's ClassId to a class name; regions
        /// with no class fall back to their own name, which is what makes the quick workflow
        /// (draw a box, type "Staff") work without creating classes up front.
        /// </summary>
        public static List<CropResult> ExportCrops(
            Mat source,
            IEnumerable<CalibrationObjectBase> regions,
            string outputFolder,
            string sourceImageName,
            Func<string?, string?> classNameResolver,
            int paddingPx = 0)
        {
            var results = new List<CropResult>();
            var stamp = DateTime.Now.ToString("HHmmss");
            var baseName = SanitiseSegment(Path.GetFileNameWithoutExtension(sourceImageName));
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "frame";

            int index = 0;
            foreach (var region in regions)
            {
                index++;
                if (!region.IsVisible) continue;

                // A line encloses no area, so there is nothing to crop.
                if (region is LineObject)
                {
                    results.Add(new CropResult
                    {
                        RegionName = region.Name,
                        Skipped = "lines have no area to crop"
                    });
                    continue;
                }

                var label = classNameResolver(region.ClassId);
                if (string.IsNullOrWhiteSpace(label)) label = region.Name;
                var className = SanitiseSegment(label);
                if (string.IsNullOrWhiteSpace(className)) className = "unlabelled";

                var rect = ToPixelRect(region.GetBounds(), source.Width, source.Height, paddingPx);
                if (rect.Width < 2 || rect.Height < 2)
                {
                    results.Add(new CropResult
                    {
                        RegionName = region.Name,
                        ClassName = className,
                        Skipped = "region is too small once clamped to the image"
                    });
                    continue;
                }

                var classFolder = Path.Combine(outputFolder, className);
                Directory.CreateDirectory(classFolder);

                // Index and timestamp keep repeated exports of the same frame from overwriting
                // each other, which is easy to do when building a set from a video.
                var file = Path.Combine(classFolder, $"{baseName}_{index:D2}_{stamp}.jpg");
                file = MakeUnique(file);

                using var crop = new Mat(source, rect);
                Cv2.ImWrite(file, crop);

                results.Add(new CropResult
                {
                    RegionName = region.Name,
                    ClassName = className,
                    SavedPath = file,
                    SourceImage = sourceImageName,
                    X = rect.X,
                    Y = rect.Y,
                    Width = rect.Width,
                    Height = rect.Height,
                    SavedUtc = DateTime.UtcNow
                });
            }

            return results;
        }

        /// <summary>Bounds to an integer pixel rect, padded then clamped inside the image.</summary>
        private static Rect ToPixelRect(System.Windows.Rect bounds, int imageWidth, int imageHeight, int padding)
        {
            int x = (int)Math.Floor(bounds.X) - padding;
            int y = (int)Math.Floor(bounds.Y) - padding;
            int w = (int)Math.Ceiling(bounds.Width) + padding * 2;
            int h = (int)Math.Ceiling(bounds.Height) + padding * 2;

            if (x < 0) { w += x; x = 0; }
            if (y < 0) { h += y; y = 0; }
            if (x + w > imageWidth) w = imageWidth - x;
            if (y + h > imageHeight) h = imageHeight - y;

            return new Rect(x, y, Math.Max(0, w), Math.Max(0, h));
        }

        /// <summary>Strips characters that are not legal in a folder or file name.</summary>
        private static string SanitiseSegment(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(value.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return cleaned.Trim('.', ' ');
        }

        private static string MakeUnique(string path)
        {
            if (!File.Exists(path)) return path;

            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (int n = 2; ; n++)
            {
                var candidate = Path.Combine(dir, $"{name}_{n}{ext}");
                if (!File.Exists(candidate)) return candidate;
            }
        }
    }
}
