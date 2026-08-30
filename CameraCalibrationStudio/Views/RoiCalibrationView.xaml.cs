using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CameraCalibrationStudio.Controls;
using CameraCalibrationStudio.Models;
using CameraCalibrationStudio.Models.Roi;
using CameraCalibrationStudio.Services;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using Path = System.IO.Path;
using Point = System.Windows.Point;
using Window = System.Windows.Window;

namespace CameraCalibrationStudio.Views
{
    /// <summary>
    /// The primary calibration workspace: toolbar + draw tools + canvas + live object list +
    /// live JSON panel + non-destructive adjustments/color + status bar. This view owns the
    /// single authoritative RoiDocument; the canvas, object list and JSON panel all render
    /// from it and nothing else, so they can never drift out of sync with each other.
    ///
    /// Selection is the default interaction whenever no drawing tool is active — there is no
    /// separate "Select" tool. Choosing Rectangle/Square/Polygon/Line draws exactly one shape
    /// and then automatically returns to normal select behavior (see ResetToolToSelect).
    /// </summary>
    public partial class RoiCalibrationView : UserControl
    {
        private readonly RoiDocument _document = new();
        private readonly RoiHistory _history = new();
        private readonly PreviewProcessor _preview = new();
        private readonly ObservableCollection<CalibrationClass> _classLibrary = ClassLibraryStore.Load();
        private Mat? _originalMat;
        private bool _dirty;
        private bool _syncingSelection;
        private Point? _lastHover;
        private double _lastJsonColumnWidth = 360;
        private bool _jsonCollapsed;
        private CalibrationClass? _activeClass;
        private ICollectionView? _objectListView;
        private string? _classFilterId; // null = "All Classes"

        public bool IsDirty => _dirty;
        public string? CurrentImagePath => _document.ImagePath;
        public RoiDocument Document => _document;

        public RoiCalibrationView()
        {
            InitializeComponent();

            Canvas.Document = _document;
            Canvas.History = _history;
            Canvas.ClassColorResolver = GetClassColor;
            Canvas.Changed += OnCanvasChanged;
            Canvas.SelectionChanged += OnCanvasSelectionChanged;
            Canvas.RequestNaming += OnRequestNaming;
            Canvas.HoverPositionChanged += p => { _lastHover = p; UpdateStatusBar(); };
            Canvas.ZoomChanged += _ => UpdateStatusBar();

            _objectListView = CollectionViewSource.GetDefaultView(_document.Objects);
            _objectListView.Filter = o => _classFilterId == null || (o is CalibrationObjectBase obj && obj.ClassId == _classFilterId);
            ObjectList.ItemsSource = _objectListView;
            FilterGallery.ItemsSource = _filters;

            UpdateToolHint();
            UpdateActiveClassDisplay();
            RefreshRecentClassChips();
            RefreshClassFilterCombo();
            UpdateStatusBar();
        }

        // =====================================================================
        // Class library
        // =====================================================================

        private Color? GetClassColor(CalibrationObjectBase obj)
        {
            if (obj.ClassId == null) return null;
            var cls = _classLibrary.FirstOrDefault(c => c.Id == obj.ClassId);
            if (cls == null) return null;
            try { return (Color)ColorConverter.ConvertFromString(cls.ColorHex); }
            catch { return null; }
        }

        /// <summary>Keeps each object's list-item color swatch (a plain bound property) in sync with the class library.</summary>
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

            if (cls?.PreferredShape != null)
            {
                var radio = cls.PreferredShape switch
                {
                    ShapeKind.Rectangle => ToolRectangle,
                    ShapeKind.Square => ToolSquare,
                    ShapeKind.Polygon => ToolPolygon,
                    ShapeKind.Line => ToolLine,
                    _ => null
                };
                if (radio != null) radio.IsChecked = true;
            }
        }

        private void UpdateActiveClassDisplay()
        {
            if (_activeClass == null)
            {
                ActiveClassText.Text = "No active class — pick one, or draw and choose after";
                ActiveClassSwatch.Fill = (Brush)FindResource("TextSecondary");
            }
            else
            {
                ActiveClassText.Text = _activeClass.Name + (_activeClass.PreferredShape != null ? $"  ·  {_activeClass.PreferredShape}" : "");
                ActiveClassSwatch.Fill = _activeClass.SwatchBrush;
            }
        }

        private void RefreshRecentClassChips()
        {
            RecentClassesPanel.Children.Clear();
            var recent = _classLibrary
                .Where(c => c.LastUsedUtc != null)
                .OrderByDescending(c => c.LastUsedUtc)
                .Take(4);

            foreach (var c in recent)
            {
                var chip = new Border
                {
                    Background = (Brush)FindResource("TileBackground"),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0, 0, 4, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = c.Name
                };
                var stack = new StackPanel { Orientation = Orientation.Horizontal };
                stack.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = c.SwatchBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
                stack.Children.Add(new TextBlock { Text = Truncate(c.Name, 14), FontSize = 10.5, Foreground = (Brush)FindResource("TextPrimary") });
                chip.Child = stack;
                chip.MouseLeftButtonDown += (_, _) => SetActiveClass(c);
                RecentClassesPanel.Children.Add(chip);
            }
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

        private void RefreshClassFilterCombo()
        {
            var current = _classFilterId;
            ClassFilterCombo.Items.Clear();
            ClassFilterCombo.Items.Add(new ComboBoxItem { Content = "All Classes", Tag = null });
            foreach (var c in _classLibrary.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                ClassFilterCombo.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Id });

            ClassFilterCombo.SelectedIndex = 0;
            foreach (ComboBoxItem item in ClassFilterCombo.Items)
            {
                if ((string?)item.Tag == current) { ClassFilterCombo.SelectedItem = item; break; }
            }
        }

        private void ClassFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _classFilterId = (ClassFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            _objectListView?.Refresh();
        }

        private void ActiveClassButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ClassPickerDialog(_classLibrary, "Set Active Class") { Owner = Window.GetWindow(this) };
            var ok = dlg.ShowDialog();
            ClassLibraryStore.Save(_classLibrary); // persists any class created inline, even if the dialog result is used below
            RefreshRecentClassChips();
            RefreshClassFilterCombo();

            if (ok != true) return;
            if (dlg.Outcome == ClassPickerOutcome.Selected) SetActiveClass(dlg.SelectedClass);
            else if (dlg.Outcome == ClassPickerOutcome.Custom) SetActiveClass(null);
        }

        private void NewClass_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new NameShapeDialog("New class name", "", _classLibrary.Select(c => c.Name).ToList()) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;

            var newClass = new CalibrationClass { Name = dlg.ResultName, ColorHex = ClassColorPalette.NextColor(_classLibrary.Count) };
            _classLibrary.Add(newClass);
            ClassLibraryStore.Save(_classLibrary);
            RefreshClassFilterCombo();
            SetActiveClass(newClass);
        }

        private void ManageClasses_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ManageClassesDialog(_classLibrary, _document.Objects) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            if (dlg.LibraryChanged)
            {
                UpdateObjectSwatches();
                RefreshRecentChipsAndFilterAndCanvas();
            }
        }

        private void RefreshRecentChipsAndFilterAndCanvas()
        {
            RefreshRecentClassChips();
            RefreshClassFilterCombo();
            UpdateActiveClassDisplay();
            Canvas.RedrawAll();
            RefreshAll();
        }

        private void ChangeObjectClassGlyph_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not CalibrationObjectBase obj) return;

            var dlg = new ClassPickerDialog(_classLibrary, "Change Class") { Owner = Window.GetWindow(this) };
            var ok = dlg.ShowDialog();
            ClassLibraryStore.Save(_classLibrary);
            RefreshRecentClassChips();
            RefreshClassFilterCombo();
            if (ok != true || dlg.Outcome != ClassPickerOutcome.Selected || dlg.SelectedClass == null) return;

            _history.Snapshot(_document.Objects);
            obj.ClassId = dlg.SelectedClass.Id;
            if (string.IsNullOrWhiteSpace(obj.Name) || _classLibrary.Any(c => c.Name == obj.Name))
                obj.Name = dlg.SelectedClass.Name;
            UpdateObjectSwatches();
            Canvas.RedrawAll();
            _dirty = true;
            RefreshAll();
            e.Handled = true;
        }

        // =====================================================================
        // Image loading
        // =====================================================================

        private void OpenImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp" };
            if (dlg.ShowDialog() != true) return;
            LoadImageFile(dlg.FileName);
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

            var deviceId = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
            LoadImageMat(mat, Path.GetFileName(path), path, deviceId);
        }

        private async void GrabRtsp_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new RtspGrabDialog { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.CapturedFrame != null)
            {
                var stamp = DateTime.Now.ToString("HHmmss");
                LoadImageMat(dlg.CapturedFrame, $"live_frame_{stamp}.jpg", "", "");
            }
            await Task.CompletedTask;
        }

        private void GrabVideo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new VideoGrabDialog { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.CapturedFrame != null)
                LoadImageMat(dlg.CapturedFrame, dlg.SuggestedName, "", "");
        }

        /// <summary>Common entry point for both "Open Image" and "Grab Frame from RTSP" — takes ownership of mat.</summary>
        private void LoadImageMat(Mat mat, string displayName, string path, string suggestedDeviceId)
        {
            if (_dirty && !ConfirmDiscardChanges()) { mat.Dispose(); return; }

            _originalMat?.Dispose();
            _originalMat = mat;

            _document.ImageFileName = displayName;
            _document.ImagePath = path;
            _document.ImageWidth = mat.Width;
            _document.ImageHeight = mat.Height;
            if (string.IsNullOrWhiteSpace(_document.DeviceId) && !string.IsNullOrWhiteSpace(suggestedDeviceId))
                _document.DeviceId = suggestedDeviceId;

            _document.Objects.Clear();
            _history.Clear();
            _preview.SetSource(mat);
            _selectedFilterName = "";
            ResetAdjustmentControls();
            UpdateFilterThumbnails();

            var initialBitmap = _preview.Render(CurrentSettings());
            Canvas.LoadImage(initialBitmap, mat.Width, mat.Height);

            EmptyState.Visibility = Visibility.Collapsed;
            _dirty = false;
            RefreshAll();
        }

        // =====================================================================
        // Adjustments + color calibration (live, non-destructive — shared pipeline
        // with Image Editor via ImageOpsService.ApplyAdjustments)
        // =====================================================================

        private AdjustmentSettings CurrentSettings() => new()
        {
            Brightness = BrightnessSlider.Value,
            Contrast = ContrastSlider.Value,
            Sharpness = SharpnessSlider.Value,
            Temperature = TemperatureSlider.Value,
            Saturation = SaturationSlider.Value,
            Exposure = ExposureSlider.Value,
            AutoWhiteBalance = AutoWhiteBalanceCheck.IsChecked == true,
            FilterName = _selectedFilterName
        };

        // ---- Filter gallery: same shared ImageOpsService.NamedFilters set as Image Editor,
        // applied on top of brightness/contrast/sharpness/color — preview only. ----

        private string _selectedFilterName = "";

        private readonly ObservableCollection<FilterOption> _filters = BuildFilterOptions();

        private static ObservableCollection<FilterOption> BuildFilterOptions()
        {
            var list = new ObservableCollection<FilterOption> { new("None", src => src.Clone()) };
            foreach (var kv in ImageOpsService.NamedFilters)
                list.Add(new FilterOption(kv.Key, kv.Value));
            return list;
        }

        private void UpdateFilterThumbnails()
        {
            var baseMat = _preview.PreviewBase;
            if (baseMat == null)
            {
                foreach (var f in _filters) f.Thumbnail = null;
                return;
            }

            int longest = Math.Max(baseMat.Width, baseMat.Height);
            int tw = Math.Max(1, (int)(120.0 * baseMat.Width / longest));
            int th = Math.Max(1, (int)(120.0 * baseMat.Height / longest));
            using var thumbBase = ImageOpsService.Resize(baseMat, tw, th);

            foreach (var f in _filters)
            {
                try
                {
                    using var applied = f.Apply(thumbBase);
                    var bmp = applied.ToBitmapSource();
                    bmp.Freeze();
                    f.Thumbnail = bmp;
                }
                catch { f.Thumbnail = null; }
            }
        }

        private void FilterTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: FilterOption filter } || !_document.HasImage) return;
            _selectedFilterName = filter.Name == "None" ? "" : filter.Name;
            RenderPreview();
        }

        private void Adjustment_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Guards against sliders declared early in the tree (e.g. Brightness) firing their
            // initial Value="0" during InitializeComponent, before controls declared later
            // (e.g. the Color Calibration section's value labels) exist yet.
            if (ExposureValueText == null) return;

            BrightnessValueText.Text = ((int)BrightnessSlider.Value).ToString();
            ContrastValueText.Text = ((int)ContrastSlider.Value).ToString();
            SharpnessValueText.Text = ((int)SharpnessSlider.Value).ToString();
            TemperatureValueText.Text = ((int)TemperatureSlider.Value).ToString();
            SaturationValueText.Text = ((int)SaturationSlider.Value).ToString();
            ExposureValueText.Text = ((int)ExposureSlider.Value).ToString();
            if (!_document.HasImage) return;
            RenderPreview();
        }

        private void ColorAdjustment_Changed(object sender, RoutedEventArgs e)
        {
            if (!_document.HasImage) return;
            RenderPreview();
        }

        private void RenderPreview()
        {
            if (!_document.HasImage) return;
            var settings = CurrentSettings();
            _document.Adjustments = settings;
            Canvas.SetPreviewImage(_preview.Render(settings));
        }

        private void ResetAdjustmentControls()
        {
            BrightnessSlider.Value = 0;
            ContrastSlider.Value = 0;
            SharpnessSlider.Value = 100;
            TemperatureSlider.Value = 0;
            SaturationSlider.Value = 0;
            ExposureSlider.Value = 0;
            AutoWhiteBalanceCheck.IsChecked = false;
        }

        private void ResetAdjustments_Click(object sender, RoutedEventArgs e)
        {
            BrightnessSlider.Value = 0;
            ContrastSlider.Value = 0;
            SharpnessSlider.Value = 100;
        }

        private void ResetColor_Click(object sender, RoutedEventArgs e)
        {
            TemperatureSlider.Value = 0;
            SaturationSlider.Value = 0;
            ExposureSlider.Value = 0;
            AutoWhiteBalanceCheck.IsChecked = false;
        }

        private void IncludeInJson_Changed(object sender, RoutedEventArgs e)
        {
            _document.IncludeAdjustmentsInJson = IncludeAdjustmentsInJsonCheck.IsChecked == true;
            RefreshAll();
        }

        // =====================================================================
        // Draw tools — selection is the default; a creation tool auto-reverts
        // to it after one shape is finished.
        // =====================================================================

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (Canvas == null) return; // fires during InitializeComponent before Canvas exists

            Canvas.Tool = sender switch
            {
                _ when sender == ToolRectangle => ToolMode.Rectangle,
                _ when sender == ToolSquare => ToolMode.Square,
                _ when sender == ToolPolygon => ToolMode.Polygon,
                _ when sender == ToolLine => ToolMode.Line,
                _ when sender == ToolPan => ToolMode.Pan,
                _ => ToolMode.Select
            };
            UpdateToolHint();
            Canvas.RedrawAll();
        }

        /// <summary>Returns to normal select/edit behavior — called after a shape is created or cancelled.</summary>
        private void ResetToolToSelect()
        {
            ToolRectangle.IsChecked = false;
            ToolSquare.IsChecked = false;
            ToolPolygon.IsChecked = false;
            ToolLine.IsChecked = false;
            ToolPan.IsChecked = false;
            Canvas.Tool = ToolMode.Select;
            UpdateToolHint();
        }

        private void UpdateToolHint()
        {
            ToolHintText.Text = Canvas.Tool switch
            {
                ToolMode.Rectangle => "Rectangle: Click and drag to create a region.",
                ToolMode.Square => "Square: Click and drag — width and height stay equal automatically.",
                ToolMode.Polygon => "Polygon: Click to add points • Enter or double-click to finish • Esc to cancel • Backspace removes the last point.",
                ToolMode.Line => "Line: Click and drag from the start point to the end point.",
                ToolMode.Pan => "Pan: Click and drag to move around the image. Mouse wheel zooms.",
                _ => "Click a region to select it • drag its point handles to edit • double-click a polygon edge to add a point • Ctrl+drag inside it to move it."
            };
        }

        // =====================================================================
        // Naming / creation
        // =====================================================================

        private void OnRequestNaming(CalibrationObjectBase pending)
        {
            // Workflow B (class-first): an active class is already selected, so assign it
            // immediately with no popup at all — this is the fast, repeated-annotation path.
            if (_activeClass != null)
            {
                AssignClassToPending(pending, _activeClass);
                Canvas.CommitPendingShape();
                _dirty = true;
                FinishShapeCreation();
                return;
            }

            // Workflow A (tool-first): no active class yet — offer the fast picker, with a
            // fallback to a plain free-typed name for one-off regions.
            var picker = new ClassPickerDialog(_classLibrary) { Owner = Window.GetWindow(this) };
            var pickerResult = picker.ShowDialog();
            ClassLibraryStore.Save(_classLibrary);
            RefreshRecentClassChips();
            RefreshClassFilterCombo();

            if (pickerResult == true && picker.Outcome == ClassPickerOutcome.Selected && picker.SelectedClass != null)
            {
                AssignClassToPending(pending, picker.SelectedClass);
                Canvas.CommitPendingShape();
                _dirty = true;
                FinishShapeCreation();
                return;
            }

            // Custom: the user typed a name but left "add as new class" unticked, so the name is
            // used for this region only and nothing is written to the class library.
            if (pickerResult == true && picker.Outcome == ClassPickerOutcome.Custom
                && !string.IsNullOrWhiteSpace(picker.TypedName))
            {
                pending.Name = picker.TypedName!;
                Canvas.CommitPendingShape();
                _dirty = true;
                FinishShapeCreation();
                return;
            }

            Canvas.DiscardPendingShape();
            ResetToolToSelect();
            RefreshAll();
        }

        /// <summary>Sets the object's class + a sensible instance name (auto-numbered if the class is already used).</summary>
        private void AssignClassToPending(CalibrationObjectBase pending, CalibrationClass cls)
        {
            pending.ClassId = cls.Id;
            cls.LastUsedUtc = DateTime.UtcNow;

            var existingCount = _document.Objects.Count(o => o.ClassId == cls.Id);
            pending.Name = existingCount == 0 ? cls.Name : NextInstanceName(cls.Name);
        }

        /// <summary>Suggests "CLASS_02", "CLASS_03", … for repeated use of the same class; the technician can still rename freely.</summary>
        private string NextInstanceName(string className)
        {
            int n = 2;
            while (_document.Objects.Any(o => string.Equals(o.Name, $"{className}_{n:D2}", StringComparison.OrdinalIgnoreCase)))
                n++;
            return $"{className}_{n:D2}";
        }

        /// <summary>Common tail after a shape is committed with a class/name: refresh everything, then
        /// respect Single Draw vs Continuous Draw mode for whether the tool stays active.</summary>
        private void FinishShapeCreation()
        {
            UpdateObjectSwatches();
            RefreshRecentClassChips();
            RefreshClassFilterCombo();

            if (ContinuousDrawRadio.IsChecked != true)
                ResetToolToSelect();

            RefreshAll();
        }

        // =====================================================================
        // Object list
        // =====================================================================

        private void ObjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSelection) return;
            _syncingSelection = true;
            Canvas.Select(ObjectList.SelectedItem as CalibrationObjectBase);
            _syncingSelection = false;
        }

        // Char/string literals used by the JSON highlight scanner.
        private const char QUOTE = '"';
        private const char BACKSLASH = '\\';
        private const char OPEN_BRACE = '{';
        private const char CLOSE_BRACE = '}';
        private static readonly string NAME_KEY = "\"name\": ";

        private void OnCanvasSelectionChanged(CalibrationObjectBase? obj)
        {
            // The list sync is guarded (it re-enters via ObjectList_SelectionChanged), but the
            // JSON highlight must run on BOTH paths - selecting on the canvas and selecting in
            // the object list should each jump the JSON panel to that region.
            if (!_syncingSelection)
            {
                _syncingSelection = true;
                ObjectList.SelectedItem = obj;
                _syncingSelection = false;
            }

            HighlightInJson(obj);
        }

        /// <summary>
        /// Selects and scrolls the JSON panel to the block describing <paramref name="obj"/>, so
        /// picking a zone on the canvas reveals its calibration entry. Re-runs on every refresh
        /// during a drag, which is what makes the coordinates visibly tick over live.
        /// </summary>
        private void HighlightInJson(CalibrationObjectBase? obj)
        {
            if (JsonText == null) return;

            if (obj == null || !_document.HasImage)
            {
                JsonText.Select(0, 0);
                return;
            }

            var text = JsonText.Text;
            // Match the same escaping the JSON writer produces for the name value.
            var needle = NAME_KEY + System.Text.Json.JsonSerializer.Serialize(obj.Name);
            int nameIdx = text.IndexOf(needle, StringComparison.Ordinal);
            if (nameIdx < 0) return;

            int start = text.LastIndexOf(OPEN_BRACE, nameIdx);
            if (start < 0) start = nameIdx;
            int end = FindMatchingBrace(text, start);
            int length = end > start ? end - start + 1 : needle.Length;

            JsonText.Select(start, length);

            // Show a couple of lines of lead-in so the block reads in context rather than being
            // pinned to the very top edge of the panel.
            int line = JsonText.GetLineIndexFromCharacterIndex(start);
            JsonText.ScrollToLine(Math.Max(0, line - 2));
        }

        /// <summary>Index of the closing brace matching the one at <paramref name="openIndex"/>, or -1. String-aware.</summary>
        private static int FindMatchingBrace(string text, int openIndex)
        {
            int depth = 0;
            bool inString = false, escaped = false;

            for (int i = openIndex; i < text.Length; i++)
            {
                char c = text[i];

                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == BACKSLASH) escaped = true;
                    else if (c == QUOTE) inString = false;
                    continue;
                }

                if (c == QUOTE) inString = true;
                else if (c == OPEN_BRACE) depth++;
                else if (c == CLOSE_BRACE && --depth == 0) return i;
            }
            return -1;
        }

        private void DeleteObjectGlyph_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not CalibrationObjectBase obj) return;
            _history.Snapshot(_document.Objects);
            _document.Objects.Remove(obj);
            if (Canvas.Selected == obj) Canvas.Select(null);
            Canvas.RedrawAll();
            _dirty = true;
            RefreshAll();
            e.Handled = true;
        }

        private void ObjectName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return; // single click just selects via the ListBox as usual
            if (sender is not FrameworkElement fe || fe.DataContext is not CalibrationObjectBase obj) return;
            RenameObject(obj);
            e.Handled = true;
        }

        private void RenameObjectGlyph_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not CalibrationObjectBase obj) return;
            RenameObject(obj);
            e.Handled = true;
        }

        private void RenameObject(CalibrationObjectBase obj)
        {
            var otherNames = _document.Objects.Where(o => o != obj).Select(o => o.Name).ToList();
            var dlg = new NameShapeDialog("Rename region", obj.Name, otherNames, isRename: true)
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() != true) return;

            _history.Snapshot(_document.Objects);
            obj.Name = dlg.ResultName;
            _dirty = true;
            Canvas.RedrawAll();
            RefreshAll();
        }

        private void DuplicateObjectGlyph_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not CalibrationObjectBase obj) return;

            var clone = obj.Clone();
            clone.Translate(24, 24); // offset so the copy doesn't sit exactly on top of the original
            clone.Name = NextAvailableName(obj.Name + " copy");

            _history.Snapshot(_document.Objects);
            _document.Objects.Add(clone);
            UpdateObjectSwatches();
            Canvas.RedrawAll();
            Canvas.Select(clone);
            _dirty = true;
            RefreshAll();
            e.Handled = true;
        }

        private string NextAvailableName(string baseName)
        {
            if (_document.Objects.All(o => !string.Equals(o.Name, baseName, StringComparison.OrdinalIgnoreCase)))
                return baseName;

            int n = 2;
            while (_document.Objects.Any(o => string.Equals(o.Name, $"{baseName} {n}", StringComparison.OrdinalIgnoreCase)))
                n++;
            return $"{baseName} {n}";
        }

        // =====================================================================
        // Canvas change notifications
        // =====================================================================

        private void OnCanvasChanged()
        {
            _dirty = true;
            RefreshAll();
        }

        // =====================================================================
        // Undo / Redo
        // =====================================================================

        private void Undo_Click(object sender, RoutedEventArgs e) => DoUndo();
        private void Redo_Click(object sender, RoutedEventArgs e) => DoRedo();

        public void DoUndo()
        {
            var snapshot = _history.Undo(_document.Objects);
            if (snapshot == null) return;
            Canvas.ReplaceObjects(snapshot);
            UpdateObjectSwatches();
            _dirty = true;
            RefreshAll();
        }

        public void DoRedo()
        {
            var snapshot = _history.Redo(_document.Objects);
            if (snapshot == null) return;
            Canvas.ReplaceObjects(snapshot);
            UpdateObjectSwatches();
            _dirty = true;
            RefreshAll();
        }

        // =====================================================================
        // Zoom
        // =====================================================================

        private void Fit_Click(object sender, RoutedEventArgs e) => Canvas.FitToWindow();
        private void Zoom100_Click(object sender, RoutedEventArgs e) => Canvas.SetZoomPercent(100);

        // =====================================================================
        // JSON panel collapse
        // =====================================================================

        private void CollapseJson_Click(object sender, RoutedEventArgs e)
        {
            _jsonCollapsed = !_jsonCollapsed;
            if (_jsonCollapsed)
            {
                _lastJsonColumnWidth = JsonColumn.Width.Value;
                JsonColumn.MinWidth = 28;
                JsonColumn.Width = new GridLength(28);
                JsonPanel.Visibility = Visibility.Collapsed;
                JsonCollapsedStrip.Visibility = Visibility.Visible;
                JsonSplitter.Visibility = Visibility.Collapsed;
            }
            else
            {
                JsonColumn.MinWidth = 220;
                JsonColumn.Width = new GridLength(_lastJsonColumnWidth);
                JsonPanel.Visibility = Visibility.Visible;
                JsonCollapsedStrip.Visibility = Visibility.Collapsed;
                JsonSplitter.Visibility = Visibility.Visible;
            }

            // Collapsing/expanding this panel resizes the canvas but isn't a zoom action the user
            // chose — make sure the image can never end up clipped by the new, narrower viewport.
            Canvas.RefitAfterLayoutChange();
        }

        private void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
            Canvas.RefitAfterLayoutChange();

        // =====================================================================
        // Batch
        // =====================================================================

        private void Batch_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new BatchDialog(CurrentSettings()) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
        }

        // =====================================================================
        // Save / Open calibration
        // =====================================================================

        private void SaveCalibration_Click(object sender, RoutedEventArgs e) => SaveCalibration();

        public bool SaveCalibration()
        {
            var errors = RoiJsonService.Validate(_document);
            if (errors.Count > 0)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "Cannot save calibration\n\n" + string.Join("\n", errors.Select(x => "• " + x)),
                    "Save Calibration", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "Calibration JSON|*.json",
                FileName = string.IsNullOrWhiteSpace(_document.ImageFileName)
                    ? "camera_calibration.json"
                    : Path.GetFileNameWithoutExtension(_document.ImageFileName) + "_calibration.json"
            };
            if (dlg.ShowDialog() != true) return false;

            try
            {
                RoiJsonService.SaveNative(_document, dlg.FileName, ResolveClassName);
                _dirty = false;
                RefreshAll();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"Could not save the file.\n\n{ex.Message}",
                    "Save Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void OpenCalibration_Click(object sender, RoutedEventArgs e)
        {
            if (!_document.HasImage)
            {
                MessageBox.Show(Window.GetWindow(this), "Open the matching camera image first, then open its calibration file.",
                    "Open Calibration", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new OpenFileDialog { Filter = "Calibration JSON|*.json" };
            if (dlg.ShowDialog() != true) return;

            try
            {
                RoiJsonService.LoadNativeInto(_document, dlg.FileName, ResolveClassId);
                _history.Clear();
                Canvas.ReplaceObjects(_document.Objects.ToList());
                BrightnessSlider.Value = _document.Adjustments.Brightness;
                ContrastSlider.Value = _document.Adjustments.Contrast;
                SharpnessSlider.Value = _document.Adjustments.Sharpness == 0 ? 100 : _document.Adjustments.Sharpness;
                TemperatureSlider.Value = _document.Adjustments.Temperature;
                SaturationSlider.Value = _document.Adjustments.Saturation;
                ExposureSlider.Value = _document.Adjustments.Exposure;
                AutoWhiteBalanceCheck.IsChecked = _document.Adjustments.AutoWhiteBalance;
                _selectedFilterName = _document.Adjustments.FilterName;
                UpdateFilterThumbnails();
                RenderPreview();
                UpdateObjectSwatches();
                RefreshClassFilterCombo();
                _dirty = false;
                RefreshAll();
            }
            catch (CalibrationResolutionMismatchException ex)
            {
                MessageBox.Show(Window.GetWindow(this),
                    $"{ex.Message}\n\nLoading it anyway would misplace every region. Open the original {ex.FileWidth}x{ex.FileHeight} image first.",
                    "Resolution mismatch", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"Could not open this calibration file.\n\n{ex.Message}",
                    "Open Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportProduction_Click(object sender, RoutedEventArgs e)
        {
            if (!_document.HasImage) return;
            var dlg = new SaveFileDialog
            {
                Filter = "Zones JSON|*.json",
                FileName = string.IsNullOrWhiteSpace(_document.DeviceId) ? "zones.json" : $"{_document.DeviceId}.json"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                RoiJsonService.SaveProductionZones(_document, dlg.FileName);
                MessageBox.Show(Window.GetWindow(this),
                    "Exported. This uses the production journey-zones schema (normalized polygon coordinates). " +
                    "Rectangles/squares are exported as their 4-corner polygon.",
                    "Export Production Zones", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"Could not export.\n\n{ex.Message}",
                    "Export Production Zones", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string? ResolveClassName(string? classId) =>
            classId == null ? null : _classLibrary.FirstOrDefault(c => c.Id == classId)?.Name;

        private string? ResolveClassId(string className) =>
            _classLibrary.FirstOrDefault(c => string.Equals(c.Name, className, StringComparison.OrdinalIgnoreCase))?.Id;

        private void Meta_TextChanged(object sender, TextChangedEventArgs e)
        {
            _document.DeviceId = DeviceIdBox.Text.Trim();
            _document.StoreCode = StoreCodeBox.Text.Trim();
            _document.AreaId = AreaIdBox.Text.Trim();
        }

        private bool ConfirmDiscardChanges()
        {
            var result = MessageBox.Show(Window.GetWindow(this),
                "You have unsaved calibration changes.\n\nOpening a new image will discard them unless you save first.\n\nContinue without saving?",
                "Unsaved changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return result == MessageBoxResult.Yes;
        }

        // =====================================================================
        // Help / tour (invoked from the title bar's Help menu — not duplicated
        // in this toolbar)
        // =====================================================================

        public void ShowTour()
        {
            var dlg = new GuidedTourDialog { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
        }

        // Public wrappers so the main window's title bar can drive this view directly.
        public void RaiseOpenImage() => OpenImage_Click(this, new RoutedEventArgs());
        public void RaiseOpenCalibration() => OpenCalibration_Click(this, new RoutedEventArgs());
        public void RaiseFit() => Canvas.FitToWindow();
        public void RaiseZoom100() => Canvas.SetZoomPercent(100);
        public void RaiseShortcuts() => ShowShortcuts();

        public void ShowShortcuts()
        {
            MessageBox.Show(Window.GetWindow(this),
                "Ctrl+O   Open Image\n" +
                "Ctrl+S   Save Calibration\n" +
                "Ctrl+Z   Undo\n" +
                "Ctrl+Y   Redo\n" +
                "Delete   Delete selected object\n" +
                "Esc      Cancel current drawing\n" +
                "Enter    Finish polygon\n" +
                "F        Fit image to window\n" +
                "1        100% zoom\n" +
                "Space+drag / Pan tool   Pan\n" +
                "Mouse wheel             Zoom (around cursor)",
                "Keyboard Shortcuts", MessageBoxButton.OK, MessageBoxImage.None);
        }

        // =====================================================================
        // Keyboard shortcuts (global to this view; must not steal keys the
        // canvas needs for its own polygon editing — Enter/Esc/Backspace are
        // intentionally left untouched here).
        // =====================================================================

        private void RoiCalibrationView_PreviewKeyDown(object sender, KeyEventArgs e) => HandleGlobalKey(e);

        public void HandleGlobalKey(KeyEventArgs e)
        {
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool typing = Keyboard.FocusedElement is TextBox;

            if (ctrl && e.Key == Key.O) { OpenImage_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (ctrl && e.Key == Key.S) { SaveCalibration(); e.Handled = true; }
            else if (ctrl && e.Key == Key.Z) { DoUndo(); e.Handled = true; }
            else if (ctrl && e.Key == Key.Y) { DoRedo(); e.Handled = true; }
            else if (!typing && e.Key == Key.Delete) { Canvas.DeleteSelected(); e.Handled = true; }
            else if (!typing && e.Key == Key.F) { Canvas.FitToWindow(); e.Handled = true; }
            else if (!typing && e.Key == Key.D1) { Canvas.SetZoomPercent(100); e.Handled = true; }
        }

        // =====================================================================
        // Refresh (single place that keeps JSON panel + status bar + list in sync)
        // =====================================================================

        private void RefreshAll()
        {
            // Guards against controls in the left column (e.g. the "include in JSON" checkbox's
            // default IsChecked="True") firing their changed-event during InitializeComponent,
            // before JsonText further down the tree exists yet.
            if (JsonText == null) return;
            JsonText.Text = _document.HasImage ? RoiJsonService.ToPrettyString(_document, ResolveClassName) : "{\n}";
            // Rewriting Text drops any selection, so restore the highlight on the selected zone.
            // During a point drag this runs on every mouse-move, keeping the block scrolled
            // into view with its coordinates updating live.
            HighlightInJson(Canvas.Selected);
            UpdateStatusBar();
        }

        private void UpdateStatusBar()
        {
            if (!_document.HasImage)
            {
                StatusText.Text = "Open an image to begin.";
                SavedStateText.Text = "";
                return;
            }

            string coords = _lastHover.HasValue ? $"  |  X: {(int)_lastHover.Value.X}  Y: {(int)_lastHover.Value.Y}" : "";
            StatusText.Text = $"{_document.ImageFileName}  |  {_document.ImageWidth} x {_document.ImageHeight}  |  Zoom {Canvas.CurrentZoomPercent:0}%{coords}";
            SavedStateText.Text = _dirty ? "Unsaved changes" : "Saved";
        }
    }
}
