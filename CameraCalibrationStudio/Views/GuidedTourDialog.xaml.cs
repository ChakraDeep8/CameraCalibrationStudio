using System.Windows;
using Window = System.Windows.Window;

namespace CameraCalibrationStudio.Views
{
    public partial class GuidedTourDialog : Window
    {
        private record Step(string Title, string Body);

        private readonly Step[] _steps =
        {
            new("Load a camera image",
                "Start by opening a clear, in-focus image from the camera you want to calibrate.\n\n" +
                "Use File → Open Image, the Open Image button in the toolbar, Ctrl+O, or drag an image file onto the window."),
            new("Improve visibility if needed",
                "Brightness, contrast and sharpness at the bottom only improve what you see while drawing.\n\n" +
                "They never change the calibration coordinates that get saved — the original image is always used for the exported JSON."),
            new("Choose a drawing tool",
                "Rectangle / Square — for rectangular calibration areas.\n" +
                "Polygon — for irregular camera regions (click each corner, Enter to finish).\n" +
                "Line — for gate or crossing lines (drag from start to end)."),
            new("Draw directly on the image",
                "Click and drag (or click point-by-point for polygons) right on top of the image.\n\n" +
                "Coordinates are automatically stored using the original image resolution — you never need to type a number."),
            new("Assign a Class",
                "Classes are reusable names for your calibration regions.\n\n" +
                "Create names like CUSTOMER_ZONE_ROI, STAFF_ZONE_ROI, ENTRY_LINE once, then just pick them from the list after drawing — " +
                "or set an Active Class first so new shapes are labeled automatically. No more retyping the same name."),
            new("Your JSON is generated automatically",
                "The panel on the right always shows the current calibration JSON — it updates the moment you draw, move, resize, rename or delete a region.\n\n" +
                "When you're done, click Save Calibration. You're ready to start."),
        };

        private int _index;

        public GuidedTourDialog()
        {
            InitializeComponent();
            Render();
        }

        private void Render()
        {
            var step = _steps[_index];
            StepLabel.Text = $"Step {_index + 1} of {_steps.Length}";
            TitleText.Text = step.Title;
            BodyText.Text = step.Body;
            BackButton.IsEnabled = _index > 0;
            NextButton.Content = _index == _steps.Length - 1 ? "Start Calibrating" : "Next";
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_index < _steps.Length - 1) { _index++; Render(); }
            else { DialogResult = true; Close(); }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_index > 0) { _index--; Render(); }
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
