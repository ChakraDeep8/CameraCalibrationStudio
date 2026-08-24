using System;
using System.Collections.Generic;
using System.Linq;
using CameraCalibrationStudio.Models;
using OpenCvSharp;

namespace CameraCalibrationStudio.Services
{
    public record CornerDetectionResult(bool Found, Point2f[] Corners, Mat PreviewWithOverlay);

    /// <summary>
    /// Chessboard-based geometric camera calibration (standard OpenCV workflow):
    /// detect corners in several views of a chessboard, then solve for the camera
    /// matrix and distortion coefficients.
    /// </summary>
    public static class CalibrationService
    {
        public static CornerDetectionResult DetectChessboardCorners(Mat image, int boardCols, int boardRows)
        {
            var patternSize = new Size(boardCols, boardRows);
            using var gray = new Mat();
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);

            bool found = Cv2.FindChessboardCorners(gray, patternSize, out Point2f[] corners,
                ChessboardFlags.AdaptiveThresh | ChessboardFlags.NormalizeImage | ChessboardFlags.FastCheck);

            var preview = image.Clone();
            if (found)
            {
                Cv2.CornerSubPix(gray, corners, new Size(11, 11), new Size(-1, -1),
                    new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.001));
                Cv2.DrawChessboardCorners(preview, patternSize, corners, found);
            }

            return new CornerDetectionResult(found, corners, preview);
        }

        /// <summary>
        /// Runs full calibration given the chessboard corners already detected in each accepted image.
        /// </summary>
        public static CalibrationProfile Calibrate(
            IReadOnlyList<Point2f[]> imagePointsPerView,
            int boardCols, int boardRows, double squareSizeMm,
            int imageWidth, int imageHeight)
        {
            if (imagePointsPerView.Count < 3)
                throw new InvalidOperationException("At least 3 accepted chessboard views are required for calibration.");

            var singleBoard = BuildObjectPoints(boardCols, boardRows, squareSizeMm);

            var objectPointMats = new List<Mat>();
            var imagePointMats = new List<Mat>();
            foreach (var corners in imagePointsPerView)
            {
                objectPointMats.Add(Mat.FromArray(singleBoard));
                imagePointMats.Add(Mat.FromArray(corners));
            }

            var cameraMatrix = new Mat();
            var distCoeffs = new Mat();
            Mat[] rvecs, tvecs;

            double rms = Cv2.CalibrateCamera(
                objectPointMats,
                imagePointMats,
                new Size(imageWidth, imageHeight),
                cameraMatrix, distCoeffs,
                out rvecs, out tvecs);

            var cm = new double[9];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    cm[r * 3 + c] = cameraMatrix.At<double>(r, c);

            var distArr = new double[distCoeffs.Total()];
            for (int i = 0; i < distArr.Length; i++)
                distArr[i] = distCoeffs.At<double>(i);

            foreach (var m in rvecs) m.Dispose();
            foreach (var m in tvecs) m.Dispose();
            foreach (var m in objectPointMats) m.Dispose();
            foreach (var m in imagePointMats) m.Dispose();
            cameraMatrix.Dispose();
            distCoeffs.Dispose();

            return new CalibrationProfile
            {
                ImageWidth = imageWidth,
                ImageHeight = imageHeight,
                CameraMatrix = cm,
                DistCoeffs = distArr,
                ReprojectionErrorRms = rms,
                ImagesUsed = imagePointsPerView.Count,
                BoardCols = boardCols,
                BoardRows = boardRows,
                SquareSizeMm = squareSizeMm
            };
        }

        private static Point3f[] BuildObjectPoints(int cols, int rows, double squareSize)
        {
            var pts = new Point3f[cols * rows];
            int idx = 0;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    pts[idx++] = new Point3f((float)(c * squareSize), (float)(r * squareSize), 0f);
            return pts;
        }
    }
}
