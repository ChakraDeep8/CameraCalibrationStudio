using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CameraCalibrationStudio.Models.Roi;
using CameraCalibrationStudio.Services;
using Window = System.Windows.Window;

namespace CameraCalibrationStudio.Views
{
    public enum ClassPickerOutcome { Cancelled, Selected, Custom }

    /// <summary>
    /// Compact, fast class assignment popup — search, pick, or create a new class inline,
    /// without leaving the drawing flow. Also used to set the "Active Class" before drawing.
    /// </summary>
    public partial class ClassPickerDialog : Window
    {
        private readonly ObservableCollection<CalibrationClass> _library;
        private readonly ObservableCollection<CalibrationClass> _view = new();

        public ClassPickerOutcome Outcome { get; private set; } = ClassPickerOutcome.Cancelled;
        public CalibrationClass? SelectedClass { get; private set; }

        // WPF can fire a spurious Deactivated on this borderless popup in the same tick it opens —
        // e.g. the mouse-up that completed the click which opened it still bubbling to the owner's
        // custom WindowChrome. Without a guard that self-closes the dialog before the user ever
        // sees it, which is exactly what looked like "the class picker doesn't open". Only treat a
        // Deactivated as real once this window has actually been Activated at least once.
        private bool _hasActivated;

        public ClassPickerDialog(ObservableCollection<CalibrationClass> library, string title = "Assign Calibration Class")
        {
            InitializeComponent();
            _library = library;
            TitleText.Text = title;
            ClassList.ItemsSource = _view;
            RefreshView("");
            Activated += (_, _) => _hasActivated = true;
            Loaded += (_, _) => SearchBox.Focus();
        }

        private void RefreshView(string filter)
        {
            _view.Clear();
            var ordered = _library
                .OrderByDescending(c => c.LastUsedUtc ?? DateTime.MinValue)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Where(c => string.IsNullOrWhiteSpace(filter) || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
            foreach (var c in ordered) _view.Add(c);

            if (_view.Count == 0 && _library.Count == 0)
            {
                // Empty-library state is communicated via the "+ Create New Class" button itself;
                // nothing further to show in the (empty) list.
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshView(SearchBox.Text.Trim());

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && _view.Count > 0)
            {
                ClassList.Focus();
                ClassList.SelectedIndex = 0;
                (ClassList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _view.Count > 0)
            {
                Choose(_view[0]);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Cancel_Click(this, new RoutedEventArgs());
            }
        }

        private void ClassList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ClassList.SelectedItem is CalibrationClass c) Choose(c);
        }

        private void ClassList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && ClassList.SelectedItem is CalibrationClass c) Choose(c);
        }

        private void Choose(CalibrationClass c)
        {
            c.LastUsedUtc = DateTime.UtcNow;
            SelectedClass = c;
            Outcome = ClassPickerOutcome.Selected;
            DialogResult = true;
            Close();
        }

        private void CreateNewButton_Click(object sender, RoutedEventArgs e)
        {
            CreatePanel.Visibility = Visibility.Visible;
            NewClassNameBox.Text = SearchBox.Text.Trim();
            NewClassNameBox.Focus();
            NewClassNameBox.SelectAll();
        }

        private void SaveAndAssign_Click(object sender, RoutedEventArgs e)
        {
            var name = NewClassNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(this, "Enter a class name.", "New Class", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var existing = _library.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                var result = MessageBox.Show(this,
                    $"A similar class already exists:\n{existing.Name}\n\nUse the existing class instead?",
                    "Class Already Exists", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (result == MessageBoxResult.Cancel) return;
                if (result == MessageBoxResult.Yes) { Choose(existing); return; }
                // No = create anyway, falls through
            }

            ShapeKind? shape = NewClassShapeCombo.SelectedIndex switch
            {
                0 => ShapeKind.Rectangle,
                1 => ShapeKind.Square,
                2 => ShapeKind.Polygon,
                3 => ShapeKind.Line,
                _ => null
            };

            var newClass = new CalibrationClass
            {
                Name = name,
                ColorHex = ClassColorPalette.NextColor(_library.Count),
                PreferredShape = shape,
                LastUsedUtc = DateTime.UtcNow
            };
            _library.Add(newClass);
            Choose(newClass);
        }

        private void CustomName_Click(object sender, RoutedEventArgs e)
        {
            Outcome = ClassPickerOutcome.Custom;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Outcome = ClassPickerOutcome.Cancelled;
            DialogResult = false;
            Close();
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Losing focus (e.g. clicking elsewhere) closes the popup like a real popup would,
            // rather than leaving an orphaned borderless window behind — but ignore the very first
            // Deactivated if the window was never actually Activated (see _hasActivated above).
            if (_hasActivated && IsVisible && DialogResult == null) Cancel_Click(this, new RoutedEventArgs());
        }
    }

    /// <summary>Binding helpers so the ListBox can show a color swatch and shape label without a converter.</summary>
    public static class CalibrationClassDisplayExtensions
    {
    }
}
