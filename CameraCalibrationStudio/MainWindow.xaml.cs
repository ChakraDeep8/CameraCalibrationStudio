using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CameraCalibrationStudio.Models;
using CameraCalibrationStudio.Services;
using CameraCalibrationStudio.Views;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using Path = System.IO.Path;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Window = System.Windows.Window;

namespace CameraCalibrationStudio
{
    public partial class MainWindow : Window
    {
        // ---------- Editor state ----------
        // _editorBase is the current "document": only rotate/flip/crop/resize mutate it, and
        // each of those pushes an undo entry. Brightness/contrast/sharpness/filter are live,
        // non-destructive preview settings layered on top via the same PreviewProcessor/
        // AdjustmentSettings pipeline ROI Calibration uses — reconstructed from _editorBase on
        // every change, never accumulated.
        private Mat? _editorBase;
        private readonly Stack<Mat> _undoStack = new();
        private readonly PreviewProcessor _editorPreview = new();
        private bool _cropModeActive;
        private Point? _dragStart;
        private OpenCvSharp.Rect? _editorPixelSelection;

        // ---------- Lens calibration state ----------
        private readonly List<Point2f[]> _acceptedCorners = new();
        private int _calibImageWidth, _calibImageHeight;
        private CalibrationProfile? _calibrationProfile;

        public MainWindow()
        {
            InitializeComponent();

            _calibrationProfile = ProfileStore.LoadCalibration();
            RefreshCalibResultText();

            FilterGallery.ItemsSource = _filters;
            MainWindow_StateChanged(this, EventArgs.Empty);
        }

        // =====================================================================
        // EDITOR TAB — file / undo
        // =====================================================================

        private void OpenImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff" };
            if (dlg.ShowDialog() != true) return;

            var mat = Cv2.ImRead(dlg.FileName, ImreadModes.Color);
            if (mat.Empty())
            {
                MessageBox.Show(this, "Could not open that image.", "Open Image", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SetEditorImage(mat, clearUndo: true);
            EditorStatusText.Text = $"Loaded {Path.GetFileName(dlg.FileName)} ({mat.Width}x{mat.Height})";
        }

        private async void GrabRtspEditor_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new RtspGrabDialog { Owner = this };
            if (dlg.ShowDialog() == true && dlg.CapturedFrame != null)
            {
                SetEditorImage(dlg.CapturedFrame, clearUndo: true);
                EditorStatusText.Text = $"Grabbed live frame ({dlg.CapturedFrame.Width}x{dlg.CapturedFrame.Height})";
            }
            await Task.CompletedTask;
        }

        private void SaveImage_Click(object sender, RoutedEventArgs e)
        {
            if (_editorBase == null)
            {
                ShowEditorMessage("Open an image or grab an RTSP frame first.", isWarning: true);
                return;
            }
            var dlg = new SaveFileDialog { Filter = "PNG|*.png|JPEG|*.jpg", FileName = "image.png" };
            if (dlg.ShowDialog() != true) return;

            // Save reflects the current non-destructive preview (transform + adjustments + filter),
            // exactly what's on screen — but the in-memory document stays untouched (Save As never
            // overwrites the working state, only exports a copy).
            using var output = ImageOpsService.ApplyAdjustments(_editorBase, CurrentEditorSettings());
            Cv2.ImWrite(dlg.FileName, output);
            ShowEditorMessage($"Saved to {dlg.FileName}", isWarning: false);
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_undoStack.Count == 0)
            {
                ShowEditorMessage("Nothing to undo.", isWarning: true);
                return;
            }
            var previous = _undoStack.Pop();
            _editorBase?.Dispose();
            _editorBase = previous;
            _editorPreview.SetSource(_editorBase);
            UpdateEditorDisplay();
            ShowEditorMessage("Undid last operation.", isWarning: false);
        }

        private void SetEditorImage(Mat newImage, bool clearUndo)
        {
            if (clearUndo)
            {
                while (_undoStack.Count > 0) _undoStack.Pop().Dispose();
                _editorBase?.Dispose();
                ResetEditorAdjustmentControls();
            }
            else if (_editorBase != null)
            {
                _undoStack.Push(_editorBase);
            }

            _editorBase = newImage;
            _editorPreview.SetSource(newImage);
            EditorSelectionRect.Visibility = Visibility.Collapsed;
            _editorPixelSelection = null;
            UpdateEditorDisplay();
        }

        /// <summary>Applies a destructive transform (rotate/flip/crop/resize) — the only ops that mutate the document and go through undo.</summary>
        private void ApplyEditorTransform(Func<Mat, Mat> op, string label)
        {
            if (_editorBase == null)
            {
                ShowEditorMessage("Open an image or grab an RTSP frame first.", isWarning: true);
                return;
            }
            try
            {
                var result = op(_editorBase);
                SetEditorImage(result, clearUndo: false);
                ShowEditorMessage(label, isWarning: false);
            }
            catch (Exception ex)
            {
                ShowEditorMessage($"Error: {ex.Message}", isWarning: true);
            }
        }

        /// <summary>
        /// Updates the always-visible status bar. Warnings/errors also pop a message box —
        /// the status bar alone is easy to miss, which previously made failed actions
        /// (e.g. clicking Undistort with no saved profile) look like the button did nothing.
        /// </summary>
        private void ShowEditorMessage(string message, bool isWarning)
        {
            EditorStatusText.Text = message;
            EditorStatusText.Foreground = isWarning
                ? (Brush)FindResource("WarnBrush")
                : (Brush)FindResource("TextPrimary");

            if (isWarning)
                MessageBox.Show(this, message, "Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void UpdateEditorDisplay()
        {
            EditorPlaceholderText.Visibility = _editorBase == null ? Visibility.Visible : Visibility.Collapsed;
            RenderEditorPreview();
            UpdateFilterThumbnails();
        }

        // =====================================================================
        // EDITOR TAB — real-time, non-destructive adjustments + exclusive filter
        // (shared ImageOpsService.ApplyAdjustments pipeline, same as ROI Calibration)
        // =====================================================================

        private string _selectedFilterName = "";

        private AdjustmentSettings CurrentEditorSettings() => new()
        {
            Brightness = BrightnessSlider.Value,
            Contrast = ContrastSlider.Value,
            Sharpness = SharpnessSlider.Value,
            FilterName = _selectedFilterName
        };

        private void Adjustment_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            EditorBrightnessValueText.Text = ((int)BrightnessSlider.Value).ToString();
            EditorContrastValueText.Text = ((int)ContrastSlider.Value).ToString();
            EditorSharpnessValueText.Text = ((int)SharpnessSlider.Value).ToString();
            RenderEditorPreview();
        }

        private void RenderEditorPreview()
        {
            // Guards against slider Value="0" in XAML firing ValueChanged during
            // InitializeComponent, before EditorImage further down the tree exists yet.
            if (EditorImage == null) return;
            if (_editorBase == null) { EditorImage.Source = null; return; }
            EditorImage.Source = _editorPreview.Render(CurrentEditorSettings());
        }

        private void ResetAdjustmentControls_Silent()
        {
            BrightnessSlider.Value = 0;
            ContrastSlider.Value = 0;
            SharpnessSlider.Value = 100;
        }

        private void ResetEditorAdjustmentControls()
        {
            ResetAdjustmentControls_Silent();
            _selectedFilterName = "";
        }

        private void ResetAdjustments_Click(object sender, RoutedEventArgs e)
        {
            _selectedFilterName = "";
            ResetAdjustmentControls_Silent();
            RenderEditorPreview();
        }

        // =====================================================================
        // Filter gallery — exclusive selection (None + 5 real filters), auto-applies
        // =====================================================================

        private readonly ObservableCollection<FilterOption> _filters = new()
        {
            new FilterOption("None", src => src.Clone()),
            new FilterOption("Grayscale", ImageOpsService.Grayscale),
            new FilterOption("Invert", ImageOpsService.Invert),
            new FilterOption("Blur", m => ImageOpsService.GaussianBlur(m)),
            new FilterOption("Denoise", ImageOpsService.Denoise),
            new FilterOption("Edges", m => ImageOpsService.EdgeDetect(m)),
        };

        private void UpdateFilterThumbnails()
        {
            if (_editorBase == null)
            {
                foreach (var f in _filters) f.Thumbnail = null;
                return;
            }

            using var thumbBase = ImageOpsService.Resize(_editorBase,
                Math.Max(1, (int)(140.0 * _editorBase.Width / Math.Max(_editorBase.Width, _editorBase.Height))),
                Math.Max(1, (int)(140.0 * _editorBase.Height / Math.Max(_editorBase.Width, _editorBase.Height))));

            foreach (var f in _filters)
            {
                try
                {
                    using var applied = f.Apply(thumbBase);
                    var bmp = applied.ToBitmapSource();
                    bmp.Freeze();
                    f.Thumbnail = bmp;
                }
                catch
                {
                    f.Thumbnail = null; // a filter thumbnail failing shouldn't break the gallery
                }
            }
        }

        private void FilterTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: FilterOption filter }) return;
            if (_editorBase == null)
            {
                ShowEditorMessage("Open an image or grab an RTSP frame first.", isWarning: true);
                return;
            }
            _selectedFilterName = filter.Name;
            RenderEditorPreview();
            ShowEditorMessage(filter.Name == "None" ? "Filter cleared." : $"Previewing {filter.Name}.", isWarning: false);
        }

        // =====================================================================
        // Transforms
        // =====================================================================

        private void RotateCw_Click(object sender, RoutedEventArgs e) => ApplyEditorTransform(ImageOpsService.RotateClockwise90, "Rotated 90° clockwise.");
        private void RotateCcw_Click(object sender, RoutedEventArgs e) => ApplyEditorTransform(ImageOpsService.RotateCounterClockwise90, "Rotated 90° counter-clockwise.");
        private void FlipH_Click(object sender, RoutedEventArgs e) => ApplyEditorTransform(ImageOpsService.FlipHorizontal, "Flipped horizontally.");
        private void FlipV_Click(object sender, RoutedEventArgs e) => ApplyEditorTransform(ImageOpsService.FlipVertical, "Flipped vertically.");

        private void ApplyUndistort_Click(object sender, RoutedEventArgs e)
        {
            if (_editorBase == null)
            {
                ShowEditorMessage("Open an image or grab an RTSP frame first.", isWarning: true);
                return;
            }
            if (_calibrationProfile == null)
            {
                ShowEditorMessage("No lens calibration profile saved yet. Go to the Lens Calibration tab first.", isWarning: true);
                return;
            }
            ApplyEditorTransform(m => ImageOpsService.Undistort(m, _calibrationProfile.CameraMatrix, _calibrationProfile.DistCoeffs),
                "Applied lens undistortion.");
        }

        private void Resize_Click(object sender, RoutedEventArgs e)
        {
            if (_editorBase == null)
            {
                ShowEditorMessage("Open an image or grab an RTSP frame first.", isWarning: true);
                return;
            }
            var dlg = new ResizeDialog(_editorBase.Width, _editorBase.Height) { Owner = this };
            if (dlg.ShowDialog() == true)
                ApplyEditorTransform(m => ImageOpsService.Resize(m, dlg.ResultWidth, dlg.ResultHeight),
                    $"Resized to {dlg.ResultWidth}x{dlg.ResultHeight}.");
        }

        // ---- Crop: one toggle button, drag a rectangle, Enter to crop / Esc to cancel ----

        private void CropModeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_editorBase == null)
            {
                ShowEditorMessage("Open an image or grab an RTSP frame first.", isWarning: true);
                return;
            }
            if (_cropModeActive) CancelCropMode();
            else StartCropMode();
        }

        private void StartCropMode()
        {
            _cropModeActive = true;
            EditorSelectionCanvas.IsHitTestVisible = true;
            CropModeButton.Content = "Crop: drag a rectangle";
            CropHintText.Visibility = Visibility.Visible;
        }

        private void CancelCropMode()
        {
            _cropModeActive = false;
            EditorSelectionCanvas.IsHitTestVisible = false;
            EditorSelectionRect.Visibility = Visibility.Collapsed;
            _editorPixelSelection = null;
            CropModeButton.Content = "Crop";
            CropHintText.Visibility = Visibility.Collapsed;
        }

        private void CommitCrop()
        {
            if (_editorPixelSelection == null)
            {
                ShowEditorMessage("Drag a rectangle on the image first.", isWarning: true);
                return;
            }
            var selection = _editorPixelSelection.Value;
            CancelCropMode();
            ApplyEditorTransform(m => ImageOpsService.Crop(m, selection), "Cropped to selection.");
        }

        public void HandleEditorKey(KeyEventArgs e)
        {
            if (!_cropModeActive) return;
            if (e.Key == Key.Enter) { CommitCrop(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CancelCropMode(); e.Handled = true; }
        }

        // ---- Rubber-band selection: Editor tab ----

        private void EditorImage_SizeChanged(object sender, SizeChangedEventArgs e) { }

        private void SelectionCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_cropModeActive || _editorBase == null) return;
            _dragStart = e.GetPosition(EditorSelectionCanvas);
            EditorSelectionRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(EditorSelectionRect, _dragStart.Value.X);
            Canvas.SetTop(EditorSelectionRect, _dragStart.Value.Y);
            EditorSelectionRect.Width = 0;
            EditorSelectionRect.Height = 0;
            EditorSelectionCanvas.CaptureMouse();
        }

        private void SelectionCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragStart == null) return;
            var pos = e.GetPosition(EditorSelectionCanvas);
            UpdateDragRect(_dragStart.Value, pos, EditorSelectionRect);
        }

        private void SelectionCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragStart == null || _editorBase == null) return;
            EditorSelectionCanvas.ReleaseMouseCapture();
            var pos = e.GetPosition(EditorSelectionCanvas);
            var displayRect = new Rect(
                Math.Min(_dragStart.Value.X, pos.X), Math.Min(_dragStart.Value.Y, pos.Y),
                Math.Abs(pos.X - _dragStart.Value.X), Math.Abs(pos.Y - _dragStart.Value.Y));
            _dragStart = null;

            _editorPixelSelection = MapDisplayRectToImagePixels(displayRect,
                EditorImage.ActualWidth, EditorImage.ActualHeight, _editorBase.Width, _editorBase.Height);
        }

        private static void UpdateDragRect(Point start, Point current, Rectangle rect)
        {
            var x = Math.Min(start.X, current.X);
            var y = Math.Min(start.Y, current.Y);
            rect.Width = Math.Abs(current.X - start.X);
            rect.Height = Math.Abs(current.Y - start.Y);
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
        }

        /// <summary>Maps a rectangle drawn in a Stretch=Uniform Image's own coordinate space to source pixel coordinates.</summary>
        private static OpenCvSharp.Rect? MapDisplayRectToImagePixels(Rect displayRect, double containerW, double containerH, int imgW, int imgH)
        {
            if (containerW <= 0 || containerH <= 0 || imgW <= 0 || imgH <= 0) return null;

            double scale = Math.Min(containerW / imgW, containerH / imgH);
            double dispW = imgW * scale, dispH = imgH * scale;
            double offsetX = (containerW - dispW) / 2.0, offsetY = (containerH - dispH) / 2.0;

            double px = (displayRect.X - offsetX) / scale;
            double py = (displayRect.Y - offsetY) / scale;
            double pw = displayRect.Width / scale;
            double ph = displayRect.Height / scale;

            var rect = new OpenCvSharp.Rect((int)Math.Round(px), (int)Math.Round(py), (int)Math.Round(pw), (int)Math.Round(ph));
            var bounds = new OpenCvSharp.Rect(0, 0, imgW, imgH);
            var clipped = rect.Intersect(bounds);
            return clipped.Width > 2 && clipped.Height > 2 ? clipped : null;
        }

        // =====================================================================
        // LENS CALIBRATION TAB
        // =====================================================================

        private bool TryGetBoardSettings(out int cols, out int rows, out double squareSize)
        {
            cols = rows = 0; squareSize = 0;
            if (!int.TryParse(BoardColsBox.Text, out cols) || cols < 3) return false;
            if (!int.TryParse(BoardRowsBox.Text, out rows) || rows < 3) return false;
            if (!double.TryParse(SquareSizeBox.Text, out squareSize) || squareSize <= 0) return false;
            return true;
        }

        private void AddCalibImagesFromFile_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetBoardSettings(out var cols, out var rows, out _))
            {
                MessageBox.Show(this, "Enter valid board columns/rows/square size first.", "Lens Calibration",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp", Multiselect = true };
            if (dlg.ShowDialog() != true) return;

            foreach (var file in dlg.FileNames)
            {
                using var img = Cv2.ImRead(file, ImreadModes.Color);
                if (img.Empty()) continue;
                TryAcceptCalibView(img, cols, rows, Path.GetFileName(file));
            }
        }

        private async void GrabRtspCalib_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetBoardSettings(out var cols, out var rows, out _))
            {
                MessageBox.Show(this, "Enter valid board columns/rows/square size first.", "Lens Calibration",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new RtspGrabDialog { Owner = this };
            if (dlg.ShowDialog() == true && dlg.CapturedFrame != null)
            {
                using var frame = dlg.CapturedFrame;
                TryAcceptCalibView(frame, cols, rows, "live frame");
            }
            await Task.CompletedTask;
        }

        private void TryAcceptCalibView(Mat image, int cols, int rows, string sourceLabel)
        {
            var result = CalibrationService.DetectChessboardCorners(image, cols, rows);
            CalibPreviewImage.Source = result.PreviewWithOverlay.ToBitmapSource();
            result.PreviewWithOverlay.Dispose();

            if (!result.Found)
            {
                CalibResultText.Text = $"Chessboard NOT found in {sourceLabel}. Try a clearer, well-lit, flat view of the board.\n\n{CalibResultText.Text}";
                return;
            }

            _acceptedCorners.Add(result.Corners);
            _calibImageWidth = image.Width;
            _calibImageHeight = image.Height;
            CalibViewsCountText.Text = $"{_acceptedCorners.Count} view(s) accepted";
            CalibResultText.Text = $"Accepted view from {sourceLabel} ({image.Width}x{image.Height}).\n\n{CalibResultText.Text}";
        }

        private void ClearCalibViews_Click(object sender, RoutedEventArgs e)
        {
            _acceptedCorners.Clear();
            CalibViewsCountText.Text = "0 views accepted";
            CalibResultText.Text = "Cleared accepted views.";
        }

        private void RunCalibration_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetBoardSettings(out var cols, out var rows, out var squareSize))
            {
                MessageBox.Show(this, "Enter valid board columns/rows/square size.", "Lens Calibration",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_acceptedCorners.Count < 3)
            {
                MessageBox.Show(this, "Add at least 3 accepted chessboard views before calibrating (10-20 recommended, from varied angles).",
                    "Lens Calibration", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _calibrationProfile = CalibrationService.Calibrate(_acceptedCorners, cols, rows, squareSize, _calibImageWidth, _calibImageHeight);
                RefreshCalibResultText();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Calibration failed: {ex.Message}", "Lens Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveCalibProfile_Click(object sender, RoutedEventArgs e)
        {
            if (_calibrationProfile == null)
            {
                MessageBox.Show(this, "Run a calibration first.", "Lens Calibration", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ProfileStore.SaveCalibration(_calibrationProfile);
            MessageBox.Show(this, "Lens calibration profile saved. It will now be used by \"Undistort\" and Batch Apply.",
                "Lens Calibration", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void RefreshCalibResultText()
        {
            if (_calibrationProfile == null)
            {
                CalibResultText.Text = "No calibration run yet.";
                return;
            }
            var p = _calibrationProfile;
            CalibResultText.Text =
                $"RMS reprojection error: {p.ReprojectionErrorRms:0.0000}\n" +
                $"Images used: {p.ImagesUsed}\n" +
                $"Image size: {p.ImageWidth}x{p.ImageHeight}\n" +
                $"Board: {p.BoardCols}x{p.BoardRows}, square {p.SquareSizeMm}mm\n\n" +
                $"Camera matrix:\n" +
                $"  fx={p.CameraMatrix[0]:0.00}  cx={p.CameraMatrix[2]:0.00}\n" +
                $"  fy={p.CameraMatrix[4]:0.00}  cy={p.CameraMatrix[5]:0.00}\n\n" +
                $"Distortion coeffs:\n  {string.Join(", ", p.DistCoeffs.Select(d => d.ToString("0.0000")))}";
        }

        // =====================================================================
        // Window lifetime
        // =====================================================================

        protected override void OnClosed(EventArgs e)
        {
            _editorBase?.Dispose();
            _editorPreview.Dispose();
            while (_undoStack.Count > 0) _undoStack.Pop().Dispose();
            base.OnClosed(e);
        }

        // =====================================================================
        // Custom borderless title bar (WindowChrome)
        //
        // WindowStyle="None" + a WindowChrome caption region replaces the native
        // title bar entirely — our own WPF elements always paint immediately,
        // which sidesteps the whole class of "native buttons missing until
        // resize" DWM timing bugs rather than working around it after the fact.
        // =====================================================================

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            // A maximized WindowStyle=None window otherwise renders a few pixels past the
            // visible work area on Windows; inset the content by the standard resize-border
            // thickness while maximized, and remove the inset again when restored.
            RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

            RestoreButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            RestoreButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        }

        // =====================================================================
        // Unsaved-changes protection (ROI Calibration tab)
        // =====================================================================

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (RoiView == null || !RoiView.IsDirty) return;

            var result = MessageBox.Show(this,
                "You have unsaved calibration changes.",
                "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (result == MessageBoxResult.Yes)
            {
                if (!RoiView.SaveCalibration()) e.Cancel = true; // user cancelled the save dialog
            }
        }

        // =====================================================================
        // Help (title bar button — the only place Guided Tour / Shortcuts live;
        // not duplicated in any per-tab toolbar)
        // =====================================================================

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { Background = (Brush)FindResource("PanelBackground"), BorderThickness = new Thickness(0) };
            var tour = new MenuItem { Header = "Guided Tour", Foreground = (Brush)FindResource("TextPrimary") };
            tour.Click += (_, _) => RoiView.ShowTour();
            var shortcuts = new MenuItem { Header = "Keyboard Shortcuts", Foreground = (Brush)FindResource("TextPrimary") };
            shortcuts.Click += (_, _) => RoiView.ShowShortcuts();
            menu.Items.Add(tour);
            menu.Items.Add(shortcuts);
            menu.PlacementTarget = (UIElement)sender;
            menu.IsOpen = true;
        }

        // =====================================================================
        // Window-wide keyboard routing for the Image Editor tab's crop workflow
        // (ROI Calibration handles its own shortcuts internally).
        // =====================================================================

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (MainTabs.SelectedIndex == 1) // Image Editor
                HandleEditorKey(e);
        }
    }
}
