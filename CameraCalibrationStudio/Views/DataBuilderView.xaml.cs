using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CameraCalibrationStudio.Controls;
using CameraCalibrationStudio.Models.Roi;
using CameraCalibrationStudio.Services;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using Path = System.IO.Path;
using Window = System.Windows.Window;

namespace CameraCalibrationStudio.Views
{
    /// <summary>
    /// Builds an image-classification dataset from camera frames. Same drawing surface as ROI
    /// Calibration, but instead of exporting geometry as JSON it exports the PIXELS: each drawn
    /// region is written out as its own image file, into a folder named after its label.
    ///
    /// Deliberately has no adjustment or filter controls. A training set wants the frame as the
    /// camera saw it; silently baking a brightness curve into some crops and not others is the
    /// kind of thing that quietly poisons a dataset.
    /// </summary>
    public partial class DataBuilderView : UserControl
    {
        private readonly RoiDocument _document = new();
        private readonly RoiHistory _history = new();
        private Mat? _originalMat;

        private readonly ObservableCollection<CalibrationClass> _classLibrary;
        private CalibrationClass? _activeClass;

        /// <summary>Running per-label totals for this session, shown in the sidebar.</summary>
        private readonly ObservableCollection<ClassCount> _sessionCounts = new();
        private int _sessionTotal;

        private bool _syncingSelection;

        public DataBuilderView()
        {
            InitializeComponent();

            _classLibrary = ClassLibraryStore.Load();

            Canvas.Document = _document;
            Canvas.History = _history;
            Canvas.ClassColorResolver = GetClassColor;
            Canvas.RequestNaming += OnRequestNaming;
            Canvas.SelectionChanged += OnCanvasSelectionChanged;
            Canvas.Changed += OnCanvasChanged;
            Canvas.ToolChangeRequested += OnCanvasToolChangeRequested;

            RegionList.ItemsSource = _document.Objects;
            SessionCountsList.ItemsSource = _sessionCounts;

            OutputFolderBox.Text = DataBuilderSettings.LoadOutputFolder();
            UpdateActiveClassDisplay();
            Canvas.Tool = ToolMode.Rectangle;
        }

        // =====================================================================
        // Image sources
        // =====================================================================

        private void OpenImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp" };
            if (dlg.ShowDialog() != true) return;
            LoadImageFile(dlg.FileName);
        }

        private void LoadImageFile(string path)
        {
            Mat mat;
            try
            {
                mat = Cv2.ImRead(path, ImreadModes.Color);
                if (mat.Empty()) throw new InvalidOperationException("Unsupported or corrupt image file.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"Could not open this image.\n\n{ex.Message}",
                    "Open Image", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            LoadMat(mat, Path.GetFileName(path));
        }

        private void GrabRtsp_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new RtspGrabDialog { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.CapturedFrame != null)
                LoadMat(dlg.CapturedFrame, $"live_{DateTime.Now:HHmmss}.jpg");
        }

        private void GrabVideo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new VideoGrabDialog { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.CapturedFrame != null)
                LoadMat(dlg.CapturedFrame, dlg.SuggestedName);
        }

        /// <summary>Takes ownership of <paramref name="mat"/>.</summary>
        private void LoadMat(Mat mat, string displayName)
        {
            _originalMat?.Dispose();
            _originalMat = mat;

            _document.ImageFileName = displayName;
            _document.ImageWidth = mat.Width;
            _document.ImageHeight = mat.Height;
            _document.Objects.Clear();
            _history.Clear();

            var bitmap = mat.ToBitmapSource();
            bitmap.Freeze();
            Canvas.LoadImage(bitmap, mat.Width, mat.Height);

            EmptyState.Visibility = Visibility.Collapsed;
            UpdateStatus($"{displayName}  |  {mat.Width} x {mat.Height}  |  draw a box around each subject");
        }

        private void EmptyState_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void EmptyState_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var file = files.FirstOrDefault(f =>
                new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(Path.GetExtension(f).ToLowerInvariant()));
            if (file != null) LoadImageFile(file);
        }

        // =====================================================================
        // Output folder
        // =====================================================================

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            // A save dialog stands in for a folder picker: WPF has no built-in folder browser,
            // and pulling in WinForms for one would drag its whole namespace into a WPF app and
            // collide with types like Application, Point and Rectangle.
            var dlg = new SaveFileDialog
            {
                Title = "Choose the dataset folder",
                FileName = "Select this folder",
                Filter = "Folder|*.none",
                CheckPathExists = true
            };
            if (!string.IsNullOrWhiteSpace(OutputFolderBox.Text) && Directory.Exists(OutputFolderBox.Text))
                dlg.InitialDirectory = OutputFolderBox.Text;

            if (dlg.ShowDialog() != true) return;
            var folder = Path.GetDirectoryName(dlg.FileName);
            if (!string.IsNullOrWhiteSpace(folder)) OutputFolderBox.Text = folder;
        }

        private void OutputFolder_TextChanged(object sender, TextChangedEventArgs e) =>
            DataBuilderSettings.SaveOutputFolder(OutputFolderBox.Text);

        private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = OutputFolderBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                UpdateStatus("Set an output folder first.", isWarning: true);
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }

        // =====================================================================
        // Add Data — the point of this workspace
        // =====================================================================

        private void AddData_Click(object sender, RoutedEventArgs e) => AddData();

        private void AddData()
        {
            if (_originalMat == null || !_document.HasImage)
            {
                UpdateStatus("Open an image or grab a frame first.", isWarning: true);
                return;
            }

            var folder = OutputFolderBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                UpdateStatus("Set an output folder before adding data.", isWarning: true);
                OutputFolderBox.Focus();
                return;
            }

            var regions = _document.Objects.Where(o => o.IsVisible).ToList();
            if (regions.Count == 0)
            {
                UpdateStatus("Draw at least one region first.", isWarning: true);
                return;
            }

            List<CropResult> results;
            try
            {
                Directory.CreateDirectory(folder);
                results = CropExportService.ExportCrops(
                    _originalMat, regions, folder, _document.ImageFileName,
                    ResolveClassName, (int)PaddingSlider.Value);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"Could not write the crops.\n\n{ex.Message}",
                    "Add Data", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var saved = results.Where(r => r.Saved).ToList();
            foreach (var group in saved.GroupBy(r => r.ClassName))
                BumpCount(group.Key, group.Count());
            _sessionTotal += saved.Count;

            var skipped = results.Where(r => !r.Saved).ToList();
            var message = saved.Count == 1 ? "Saved 1 crop" : $"Saved {saved.Count} crops";
            message += $" to {folder}";
            if (skipped.Count > 0)
                message += $"  |  skipped {skipped.Count}: {string.Join("; ", skipped.Select(s => $"{s.RegionName} ({s.Skipped})"))}";

            UpdateStatus(message);
            RefreshSessionTotal();

            // Clearing by default is what makes this quick to repeat across frames, and it stops
            // a second click on Add Data from writing the same crops twice.
            if (ClearAfterAddCheck.IsChecked == true && saved.Count > 0)
            {
                _history.Snapshot(_document.Objects);
                _document.Objects.Clear();
                Canvas.Select(null);
                Canvas.RedrawAll();
            }
        }

        private void BumpCount(string className, int delta)
        {
            var existing = _sessionCounts.FirstOrDefault(c =>
                string.Equals(c.ClassName, className, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { existing.Count += delta; return; }
            _sessionCounts.Add(new ClassCount { ClassName = className, Count = delta });
        }

        private void RefreshSessionTotal() =>
            SessionTotalText.Text = _sessionTotal == 0
                ? ""
                : $"{_sessionTotal} crop{(_sessionTotal == 1 ? "" : "s")} saved this session";

        // =====================================================================
        // Labels (reuses the shared class library)
        // =====================================================================

        private string? ResolveClassName(string? classId) =>
            classId == null ? null : _classLibrary.FirstOrDefault(c => c.Id == classId)?.Name;

        private Color? GetClassColor(CalibrationObjectBase obj)
        {
            if (obj.ClassId == null) return null;
            var cls = _classLibrary.FirstOrDefault(c => c.Id == obj.ClassId);
            if (cls == null) return null;
            try { return (Color)ColorConverter.ConvertFromString(cls.ColorHex); }
            catch { return null; }
        }

        private void UpdateObjectSwatches()
        {
            foreach (var obj in _document.Objects)
            {
                var color = GetClassColor(obj);
                obj.SwatchBrush = color.HasValue ? new SolidColorBrush(color.Value) : Brushes.Gray;
            }
        }

        private void SetActiveClass(CalibrationClass? cls)
        {
            _activeClass = cls;
            UpdateActiveClassDisplay();
        }

        private void UpdateActiveClassDisplay()
        {
            if (_activeClass == null)
            {
                ActiveClassText.Text = "No label — name each region as you draw";
                ActiveClassSwatch.Fill = (Brush)FindResource("TextSecondary");
            }
            else
            {
                ActiveClassText.Text = _activeClass.Name;
                ActiveClassSwatch.Fill = _activeClass.SwatchBrush;
            }
        }

        private void ActiveClassButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ClassPickerDialog(_classLibrary, "Set Label") { Owner = Window.GetWindow(this) };
            var ok = dlg.ShowDialog();
            ClassLibraryStore.Save(_classLibrary);
            if (ok != true) return;

            if (dlg.Outcome == ClassPickerOutcome.Selected) SetActiveClass(dlg.SelectedClass);
            else if (dlg.Outcome == ClassPickerOutcome.Custom) SetActiveClass(null);
        }

        private void NewClass_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new NameShapeDialog("New label", "", _classLibrary.Select(c => c.Name).ToList())
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() != true) return;

            var newClass = new CalibrationClass
            {
                Name = dlg.ResultName,
                ColorHex = ClassColorPalette.NextColor(_classLibrary.Count)
            };
            _classLibrary.Add(newClass);
            ClassLibraryStore.Save(_classLibrary);
            SetActiveClass(newClass);
        }

        private void ManageClasses_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ManageClassesDialog(_classLibrary, _document.Objects) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            if (!dlg.LibraryChanged) return;
            UpdateObjectSwatches();
            UpdateActiveClassDisplay();
            Canvas.RedrawAll();
        }

        // =====================================================================
        // Drawing / naming
        // =====================================================================

        private void OnRequestNaming(CalibrationObjectBase pending)
        {
            // A label already chosen? Apply it with no popup — the fast path when tagging many
            // subjects of the same kind across a set of frames.
            if (_activeClass != null)
            {
                AssignClass(pending, _activeClass);
                Commit();
                return;
            }

            var picker = new ClassPickerDialog(_classLibrary, "Label this region") { Owner = Window.GetWindow(this) };
            var result = picker.ShowDialog();
            ClassLibraryStore.Save(_classLibrary);

            if (result == true && picker.Outcome == ClassPickerOutcome.Selected && picker.SelectedClass != null)
            {
                AssignClass(pending, picker.SelectedClass);
                Commit();
                return;
            }

            if (result == true && picker.Outcome == ClassPickerOutcome.Custom
                && !string.IsNullOrWhiteSpace(picker.TypedName))
            {
                pending.Name = picker.TypedName!;
                Commit();
                return;
            }

            Canvas.DiscardPendingShape();

            void Commit()
            {
                Canvas.CommitPendingShape();
                UpdateObjectSwatches();
                UpdateStatus($"{_document.Objects.Count} region(s) ready — press Add Data to save them.");
            }
        }

        private void AssignClass(CalibrationObjectBase pending, CalibrationClass cls)
        {
            pending.ClassId = cls.Id;
            cls.LastUsedUtc = DateTime.UtcNow;

            int existing = _document.Objects.Count(o => o.ClassId == cls.Id);
            pending.Name = existing == 0 ? cls.Name : $"{cls.Name}_{existing + 1:D2}";
        }

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (Canvas == null) return;
            Canvas.Tool = sender switch
            {
                _ when sender == ToolRectangle => ToolMode.Rectangle,
                _ when sender == ToolSquare => ToolMode.Square,
                _ when sender == ToolPolygon => ToolMode.Polygon,
                _ when sender == ToolPan => ToolMode.Pan,
                _ => ToolMode.Select
            };
        }

        private void OnCanvasToolChangeRequested(ToolMode tool)
        {
            if (tool == ToolMode.Pan) { ToolPan.IsChecked = true; return; }

            // Back to drawing: the rectangle tool is the one this workspace is built around.
            ToolRectangle.IsChecked = true;
        }

        // =====================================================================
        // Region list
        // =====================================================================

        private void OnCanvasChanged()
        {
            UpdateObjectSwatches();
            UpdateStatus($"{_document.Objects.Count} region(s) ready — press Add Data to save them.");
        }

        private void OnCanvasSelectionChanged(CalibrationObjectBase? obj)
        {
            if (_syncingSelection) return;
            _syncingSelection = true;
            RegionList.SelectedItem = obj;
            _syncingSelection = false;
        }

        private void RegionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSelection) return;
            _syncingSelection = true;
            Canvas.Select(RegionList.SelectedItem as CalibrationObjectBase);
            _syncingSelection = false;
        }

        private void RenameRegion_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not CalibrationObjectBase obj) return;

            var dlg = new NameShapeDialog("Rename region", obj.Name,
                _document.Objects.Where(o => o != obj).Select(o => o.Name).ToList())
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() != true) return;

            _history.Snapshot(_document.Objects);
            obj.Name = dlg.ResultName;
            obj.ClassId = null; // a hand-typed name overrides the label it came from
            UpdateObjectSwatches();
            Canvas.RedrawAll();
            e.Handled = true;
        }

        private void DeleteRegion_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not CalibrationObjectBase obj) return;
            _history.Snapshot(_document.Objects);
            _document.Objects.Remove(obj);
            Canvas.Select(null);
            Canvas.RedrawAll();
            e.Handled = true;
        }

        // =====================================================================
        // Toolbar / shortcuts
        // =====================================================================

        private void Undo_Click(object sender, RoutedEventArgs e) => DoUndo();
        private void Redo_Click(object sender, RoutedEventArgs e) => DoRedo();

        private void DoUndo()
        {
            var restored = _history.Undo(_document.Objects);
            if (restored == null) return;
            Canvas.ReplaceObjects(restored);
            UpdateObjectSwatches();
        }

        private void DoRedo()
        {
            var restored = _history.Redo(_document.Objects);
            if (restored == null) return;
            Canvas.ReplaceObjects(restored);
            UpdateObjectSwatches();
        }

        private void Fit_Click(object sender, RoutedEventArgs e) => Canvas.FitToWindow();
        private void Zoom100_Click(object sender, RoutedEventArgs e) => Canvas.SetZoomPercent(100);
        private void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
            Canvas.RefitAfterLayoutChange();

        private void Padding_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (PaddingValueText == null) return;
            PaddingValueText.Text = ((int)PaddingSlider.Value).ToString();
        }

        private void DataBuilderView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool typing = Keyboard.FocusedElement is TextBox;

            if (ctrl && e.Key == Key.Enter) { AddData(); e.Handled = true; }
            else if (ctrl && e.Key == Key.Z) { DoUndo(); e.Handled = true; }
            else if (ctrl && e.Key == Key.Y) { DoRedo(); e.Handled = true; }
            else if (ctrl && e.Key == Key.D)
            {
                if (Canvas.Selected != null) DuplicateRegion(Canvas.Selected);
                e.Handled = true;
            }
            else if (!typing && e.Key == Key.Delete) { Canvas.DeleteSelected(); e.Handled = true; }
            else if (!typing && e.Key == Key.F) { Canvas.FitToWindow(); e.Handled = true; }
            else if (!typing && e.Key == Key.D1) { Canvas.SetZoomPercent(100); e.Handled = true; }
        }

        private void DuplicateRegion(CalibrationObjectBase obj)
        {
            var clone = obj.Clone();
            clone.Translate(24, 24);
            clone.Name = NextName(obj.Name);

            _history.Snapshot(_document.Objects);
            _document.Objects.Add(clone);
            UpdateObjectSwatches();
            Canvas.RedrawAll();
            Canvas.Select(clone);
        }

        private string NextName(string baseName)
        {
            for (int n = 2; ; n++)
            {
                var candidate = $"{baseName}_{n:D2}";
                if (_document.Objects.All(o => !string.Equals(o.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                    return candidate;
            }
        }

        private void UpdateStatus(string message, bool isWarning = false)
        {
            StatusText.Text = message;
            StatusText.Foreground = isWarning
                ? (Brush)FindResource("ErrorBrush")
                : (Brush)FindResource("TextSecondary");
        }

        /// <summary>Per-label tally shown in the sidebar. Mutable so the count can tick up in place.</summary>
        public sealed class ClassCount : System.ComponentModel.INotifyPropertyChanged
        {
            private int _count;
            public string ClassName { get; set; } = "";
            public int Count
            {
                get => _count;
                set { _count = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Count))); }
            }
            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        }
    }
}
