using System.Windows;

namespace CameraCalibrationStudio.Views
{
    public partial class ResizeDialog : Window
    {
        public int ResultWidth { get; private set; }
        public int ResultHeight { get; private set; }

        public ResizeDialog(int currentWidth, int currentHeight)
        {
            InitializeComponent();
            WidthBox.Text = currentWidth.ToString();
            HeightBox.Text = currentHeight.ToString();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(WidthBox.Text, out var w) || w <= 0 ||
                !int.TryParse(HeightBox.Text, out var h) || h <= 0)
            {
                MessageBox.Show(this, "Enter positive whole numbers for width and height.", "Invalid size",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResultWidth = w;
            ResultHeight = h;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
