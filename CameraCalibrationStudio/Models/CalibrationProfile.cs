using System;

namespace CameraCalibrationStudio.Models
{
    /// <summary>
    /// Result of a geometric (chessboard) camera calibration: intrinsics + distortion,
    /// enough to undistort any frame captured at the same resolution.
    /// </summary>
    public class CalibrationProfile
    {
        public string Name { get; set; } = "Camera Calibration";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }

        /// <summary>Row-major 3x3 camera matrix [fx 0 cx; 0 fy cy; 0 0 1].</summary>
        public double[] CameraMatrix { get; set; } = new double[9];

        /// <summary>Distortion coefficients (k1 k2 p1 p2 k3 ...).</summary>
        public double[] DistCoeffs { get; set; } = Array.Empty<double>();

        public double ReprojectionErrorRms { get; set; }
        public int ImagesUsed { get; set; }
        public int BoardCols { get; set; }
        public int BoardRows { get; set; }
        public double SquareSizeMm { get; set; }
    }
}
