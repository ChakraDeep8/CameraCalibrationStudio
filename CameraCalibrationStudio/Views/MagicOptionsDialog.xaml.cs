using System.Windows;
using CameraCalibrationStudio.Models.Roi;

namespace CameraCalibrationStudio.Views
{
    /// <summary>
    /// Magic's pre-flight step: pick which detectors run before AiCalibrationService touches
    /// the frame. Exists mainly to keep circular-object detection opt-in — see
    /// AiDetectionOptions for why it defaults off.
    /// </summary>
    public partial class MagicOptionsDialog : Window
    {
        public AiDetectionOptions? Result { get; private set; }

        public MagicOptionsDialog()
        {
            InitializeComponent();
        }

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            Result = new AiDetectionOptions
            {
                UseYolo = YoloCheck.IsChecked == true,
                RefineOutlines = RefineOutlinesCheck.IsChecked == true,
                AnyObject = AnyObjectCheck.IsChecked == true,
                Circular = CircularCheck.IsChecked == true,
            };
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
