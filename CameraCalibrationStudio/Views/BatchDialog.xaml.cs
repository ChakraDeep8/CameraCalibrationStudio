using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CameraCalibrationStudio.Models;
using CameraCalibrationStudio.Services;
using Window = System.Windows.Window;

namespace CameraCalibrationStudio.Views
{
    /// <summary>
    /// Batch Apply — reached from ROI Calibration's "Batch…" button, not a separate top-level
    /// page. Applies the current preview adjustments (and/or a saved lens calibration profile)
    /// to every image in a folder.
    /// </summary>
    public partial class BatchDialog : Window
    {
        private readonly AdjustmentSettings _currentAdjustments;

        public BatchDialog(AdjustmentSettings currentAdjustments)
        {
            InitializeComponent();
            _currentAdjustments = currentAdjustments;

            AdjustmentsSummaryText.Text = _currentAdjustments.IsDefault
                ? "No adjustments are currently active on the preview."
                : $"Brightness {_currentAdjustments.Brightness:0}, Contrast {_currentAdjustments.Contrast:0}, " +
                  $"Sharpness {_currentAdjustments.Sharpness:0}, Temperature {_currentAdjustments.Temperature:0}, " +
                  $"Saturation {_currentAdjustments.Saturation:0}, Exposure {_currentAdjustments.Exposure:0}" +
                  (_currentAdjustments.AutoWhiteBalance ? ", Auto White Balance" : "");
        }

        private void BrowseInputFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select input folder" };
            if (dlg.ShowDialog() == true) InputFolderBox.Text = dlg.FolderName;
        }

        private void BrowseOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select output folder" };
            if (dlg.ShowDialog() == true) OutputFolderBox.Text = dlg.FolderName;
        }

        private async void RunBatch_Click(object sender, RoutedEventArgs e)
        {
            var input = InputFolderBox.Text.Trim();
            var output = OutputFolderBox.Text.Trim();
            if (!System.IO.Directory.Exists(input))
            {
                MessageBox.Show(this, "Select a valid input folder.", "Batch Apply", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(output))
            {
                MessageBox.Show(this, "Select an output folder.", "Batch Apply", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int.TryParse(ResizeWidthBox.Text, out var rw);
            int.TryParse(ResizeHeightBox.Text, out var rh);

            var calibrationProfile = ProfileStore.LoadCalibration();
            var options = new BatchOptions
            {
                ApplyUndistort = UndistortCheck.IsChecked == true,
                ApplyAdjustments = AdjustmentsCheck.IsChecked == true,
                Adjustments = _currentAdjustments,
                Resize = ResizeCheck.IsChecked == true,
                ResizeWidth = rw > 0 ? rw : 1024,
                ResizeHeight = rh > 0 ? rh : 768,
            };

            if (options.ApplyUndistort && calibrationProfile == null)
            {
                MessageBox.Show(this, "No saved Lens Calibration profile. Uncheck Undistort or run Lens Calibration first.",
                    "Batch Apply", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BatchLogText.Text = "";
            BatchProgress.Value = 0;
            var files = BatchProcessor.EnumerateImages(input).ToList();
            if (files.Count == 0)
            {
                BatchLogText.Text = "No images found in the input folder.";
                return;
            }

            var log = new System.Text.StringBuilder();
            int failures = await Task.Run(() => BatchProcessor.Run(
                input, output, options, calibrationProfile,
                onProgress: (name, idx, total) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        BatchProgress.Value = 100.0 * idx / total;
                        log.AppendLine($"[{idx}/{total}] {name}");
                        BatchLogText.Text = log.ToString();
                        BatchLogText.ScrollToEnd();
                    });
                },
                onError: (name, msg) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        log.AppendLine($"  FAILED: {name} — {msg}");
                        BatchLogText.Text = log.ToString();
                    });
                }));

            log.AppendLine();
            log.AppendLine(failures == 0
                ? $"Done. {files.Count} image(s) processed successfully."
                : $"Done with {failures} failure(s) out of {files.Count} image(s).");
            BatchLogText.Text = log.ToString();
            BatchLogText.ScrollToEnd();
        }
    }
}
