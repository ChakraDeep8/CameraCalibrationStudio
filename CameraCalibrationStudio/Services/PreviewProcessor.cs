using System;
using CameraCalibrationStudio.Models;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System.Windows.Media.Imaging;

namespace CameraCalibrationStudio.Services
{
    /// <summary>
    /// Produces a fast, non-destructive display preview via the shared ImageOpsService pipeline.
    /// A capped-resolution copy of the source is cached once per image load; every settings
    /// change re-renders from that same cached copy (never from a previously rendered frame),
    /// so nothing accumulates and dragging a slider stays smooth even on large source images.
    /// Calibration geometry is never touched by this — it is always expressed against the true
    /// original resolution, independent of preview quality.
    /// </summary>
    public class PreviewProcessor : IDisposable
    {
        private const int MaxPreviewDimension = 1600;
        private Mat? _previewBase;

        public void SetSource(Mat original)
        {
            _previewBase?.Dispose();
            int w = original.Width, h = original.Height;
            int longest = Math.Max(w, h);
            if (longest > MaxPreviewDimension)
            {
                double scale = MaxPreviewDimension / (double)longest;
                _previewBase = new Mat();
                Cv2.Resize(original, _previewBase, new Size((int)(w * scale), (int)(h * scale)), interpolation: InterpolationFlags.Area);
            }
            else
            {
                _previewBase = original.Clone();
            }
        }

        /// <summary>The cached, capped-resolution source used for previews.</summary>
        public Mat? PreviewBase => _previewBase;

        public BitmapSource Render(AdjustmentSettings settings)
        {
            if (_previewBase == null) throw new InvalidOperationException("SetSource must be called first.");

            using var result = ImageOpsService.ApplyAdjustments(_previewBase, settings);
            var bitmap = result.ToBitmapSource();
            bitmap.Freeze();
            return bitmap;
        }

        public void Dispose() => _previewBase?.Dispose();
    }
}
