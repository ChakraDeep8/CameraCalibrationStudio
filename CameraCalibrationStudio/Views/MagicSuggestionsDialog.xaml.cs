using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CameraCalibrationStudio.Models.Roi;

namespace CameraCalibrationStudio.Views
{
    /// <summary>
    /// The Magic review step: every AI suggestion lands here first, never straight on the
    /// canvas. The technician checks/unchecks each one, edits its name or picks a class from
    /// the shared library, then Apply turns only the checked ones into real, fully editable
    /// calibration objects (RoiCalibrationView.Magic_Click does that conversion).
    /// </summary>
    public partial class MagicSuggestionsDialog : Window
    {
        private readonly List<AiSuggestion> _suggestions;

        /// <summary>Bound by the per-row ComboBox via RelativeSource — the shared class library, unmodified here.</summary>
        public ObservableCollection<CalibrationClass> ClassLibrary { get; }

        /// <summary>The suggestions the technician left checked when Apply was clicked.</summary>
        public List<AiSuggestion> Accepted { get; private set; } = new();

        public MagicSuggestionsDialog(List<AiSuggestion> suggestions, ObservableCollection<CalibrationClass> classLibrary)
        {
            InitializeComponent();
            _suggestions = suggestions;
            ClassLibrary = classLibrary;

            foreach (var s in _suggestions)
            {
                // Pre-select a matching class by name so an obvious detection (e.g. a class
                // literally named "Person") doesn't need a manual pick.
                s.SelectedClass = classLibrary.FirstOrDefault(c => string.Equals(c.Name, s.DetectedLabel, System.StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(s.Name)) s.Name = s.DetectedLabel;
                s.PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(AiSuggestion.Include)) UpdateCount(); };
            }

            SuggestionList.ItemsSource = _suggestions;
            UpdateCount();

            if (_suggestions.Count == 0)
            {
                SubtitleText.Text = "Magic didn't find anything it's confident about in this frame. Try a sharper or more front-on frame, or draw regions manually.";
                ApplyButton.IsEnabled = false;
            }
        }

        private void UpdateCount() =>
            CountText.Text = $"{_suggestions.Count(s => s.Include)} of {_suggestions.Count} selected";

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var s in _suggestions) s.Include = true;
            UpdateCount();
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var s in _suggestions) s.Include = false;
            UpdateCount();
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            Accepted = _suggestions.Where(s => s.Include).ToList();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
