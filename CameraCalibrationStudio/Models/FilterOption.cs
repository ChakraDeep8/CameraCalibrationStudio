using System;
using System.ComponentModel;
using OpenCvSharp;
using Media = System.Windows.Media;

namespace CameraCalibrationStudio.Models
{
    /// <summary>One entry in the Editor's filter gallery: a name, the transform, and a live thumbnail.</summary>
    public class FilterOption : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; }
        public Func<Mat, Mat> Apply { get; }

        private Media.ImageSource? _thumbnail;
        public Media.ImageSource? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
        }

        public FilterOption(string name, Func<Mat, Mat> apply)
        {
            Name = name;
            Apply = apply;
        }
    }
}
