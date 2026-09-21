using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CameraCalibrationStudio.Models.Roi;
using OpenCvSharp;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = OpenCvSharp.Size;

namespace CameraCalibrationStudio.Services
{
    /// <summary>
    /// "Magic" — auto-calibration suggestions. Runs entirely on-device, no network call at
    /// inference time: real object classification (person, car, chair, bottle, ... — 80 COCO
    /// categories) comes from a bundled YOLOv8s ONNX model via YoloObjectDetector; the
    /// remaining detectors are classic OpenCV contour/Hough shape analysis, kept as optional
    /// extras for objects outside YOLO's vocabulary (or as a fallback if the model file is
    /// ever missing). Suggestions are always heuristic hints, never written straight into the
    /// document — the technician reviews, renames/reclassifies and can discard any of them
    /// (see MagicSuggestionsDialog).
    /// </summary>
    public static class AiCalibrationService
    {
        /// <summary>Runs the selected detector set against one frame and returns ranked, de-duplicated suggestions.</summary>
        public static List<AiSuggestion> DetectSuggestions(Mat source, AiDetectionOptions? options = null)
        {
            options ??= AiDetectionOptions.Default;

            using var gray = new Mat();
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);

            var results = new List<AiSuggestion>();

            if (options.UseYolo && YoloObjectDetector.IsAvailable)
                results.AddRange(YoloObjectDetector.Detect(source, refineOutlines: options.RefineOutlines));
            else if (options.UseYolo)
                // Model file missing/failed to load for some reason — fall back to the classic
                // HOG pedestrian detector so Magic still finds something rather than nothing.
                results.AddRange(DetectPeople(source));

            if (options.Circular) results.AddRange(DetectCircularObjects(gray));
            if (options.AnyObject) results.AddRange(DetectGenericObjects(gray, source.Width, source.Height));

            return DeDuplicate(results)
                .OrderByDescending(s => s.Confidence)
                .Take(40)
                .ToList();
        }

        // ---------------------------------------------------------------
        // People — OpenCV's default HOG + linear SVM pedestrian detector,
        // built into OpenCvSharp, no model download required. It reports a
        // signed distance from the SVM's decision boundary alongside each
        // box, not a probability — anything near zero is a coin flip, and
        // in practice that's mostly small scattered windows on clutter/
        // texture that happened to score just above the line, not people.
        // Every detection used to get the same hardcoded confidence
        // regardless of that margin, so a near-zero-confidence false
        // positive looked exactly as trustworthy in the review list as a
        // solid match — fixed by using the real weight both to filter and
        // to set the displayed confidence.
        //
        // This detector is also, by design, tuned for pedestrians seen at
        // a working distance (a person's whole body roughly upright in
        // frame) — a close-up/cropped portrait where the subject fills
        // most of the frame is outside what it was ever trained for, and
        // it will legitimately find nothing there rather than guess.
        // ---------------------------------------------------------------
        private static List<AiSuggestion> DetectPeople(Mat source)
        {
            var found = new List<AiSuggestion>();
            try
            {
                using var hog = new HOGDescriptor();
                hog.SetSVMDetector(HOGDescriptor.GetDefaultPeopleDetector());

                var rects = hog.DetectMultiScale(source, out var weights, hitThreshold: 0, winStride: new Size(8, 8),
                    padding: new Size(8, 8), scale: 1.05, groupThreshold: 2);

                for (int i = 0; i < rects.Length; i++)
                {
                    if (weights[i] < 0.5) continue; // require a real margin past the SVM boundary, not a coin flip

                    var r = rects[i];
                    found.Add(new AiSuggestion
                    {
                        DetectedLabel = "Person",
                        Source = "HOG fallback",
                        Kind = ShapeKind.Rectangle,
                        Bounds = new Rect(r.X, r.Y, r.Width, r.Height),
                        Confidence = Math.Min(0.95, 0.45 + weights[i] * 0.2),
                    });
                }
            }
            catch
            {
                // HOG can throw on degenerate frames (e.g. tiny/odd-sized) — never let Magic crash the app.
            }

            // Keep only the strongest handful — a real frame rarely has more than a few people
            // in it, and this is what stops one genuine match from being buried among several
            // marginal ones.
            return found.OrderByDescending(s => s.Confidence).Take(6).ToList();
        }

        // ---------------------------------------------------------------
        // Circular objects — Hough circle transform (wheels, round tables,
        // clocks, buttons, drums, plates...). Hough's accumulator votes are
        // probabilistic: on a cluttered real photo (door frames, chair
        // silhouettes, cable tangles) it happily "finds" circles that aren't
        // there at all, in bulk. Two things bring that back under control:
        // a much higher accumulator threshold (param2) so only strong votes
        // count, and — the part that actually matters — verifying each
        // survivor by sampling its own circumference against the Canny edge
        // map and rejecting it unless most of that rim is a real edge.
        // ---------------------------------------------------------------
        private static List<AiSuggestion> DetectCircularObjects(Mat gray)
        {
            var found = new List<AiSuggestion>();
            try
            {
                using var blurred = new Mat();
                Cv2.GaussianBlur(gray, blurred, new Size(9, 9), 2);

                // Gradient field of the (unblurred) frame — used to check that a candidate
                // circle's rim is where a real edge is AND that the edge points radially
                // in/out from the candidate's own center. A cluttered photo (door frames,
                // chair curves, cable tangles) has edges everywhere, so "is there an edge
                // near this sample point" alone lets Hough's false positives straight
                // through; requiring the edge direction to actually agree with the circle
                // is what tells a real rim apart from coincidental nearby clutter.
                using var sobelX = new Mat();
                using var sobelY = new Mat();
                Cv2.Sobel(gray, sobelX, MatType.CV_32F, 1, 0, ksize: 3);
                Cv2.Sobel(gray, sobelY, MatType.CV_32F, 0, 1, ksize: 3);

                // Very large "circles" (a big fraction of the frame) are almost always Hough
                // grabbing a loose, diffuse pattern across cluttered real-scene edges rather
                // than an actual round object's rim, so the search radius is capped well short
                // of that regime rather than at the widest the API would technically accept.
                var minRadius = Math.Max(16, Math.Min(gray.Width, gray.Height) / 24);
                var maxRadius = Math.Min(gray.Width, gray.Height) / 6;

                var circles = Cv2.HoughCircles(blurred, HoughModes.Gradient, dp: 1.5,
                    minDist: Math.Max(80, gray.Height / 4.0), param1: 120, param2: 100,
                    minRadius: minRadius, maxRadius: maxRadius);

                foreach (var c in circles)
                {
                    var (edgeCoverage, alignment) = RadialGradientAlignment(sobelX, sobelY, c.Center, c.Radius);

                    // Two independent conditions, both required: an actual edge present at
                    // nearly every sampled angle (a real rim has no gaps — clutter that merely
                    // brushes past the candidate circle usually does), AND, among those edges,
                    // most point radially. Either check alone let real-scene clutter through.
                    if (edgeCoverage < 0.85 || alignment < 0.8) continue;

                    var points = ApproximateCircle(c.Center, c.Radius, 24);
                    var bounds = BoundsOf(points);
                    found.Add(new AiSuggestion
                    {
                        DetectedLabel = "Circular object",
                        Source = "Circle",
                        Kind = ShapeKind.Polygon,
                        PolygonPoints = points,
                        Bounds = bounds,
                        Confidence = Math.Min(0.9, 0.2 + Math.Min(edgeCoverage, alignment) * 0.7),
                    });
                }
            }
            catch
            {
                // Best-effort — a noisy frame just yields zero circle suggestions.
            }

            // A real frame rarely has more than a couple of genuinely round objects in it —
            // capping here (after verification) is what stops one real circle from also
            // surfacing as three or four barely-different near-duplicate candidates.
            return found.OrderByDescending(s => s.Confidence).Take(2).ToList();
        }

        /// <summary>Fraction of sampled points around a candidate circle whose local image
        /// gradient is strong AND points within ~28° of the radial direction (toward or away
        /// from the candidate's own center, since gradient polarity depends on which side is
        /// brighter). This is what a genuine circular rim looks like; scattered clutter that
        /// merely happens to pass near the circle at various angles does not.</summary>
        /// <summary>Returns (edgeCoverage, directionalAlignment): edgeCoverage is the fraction of
        /// sampled angles where a real edge was found at all near the candidate radius;
        /// directionalAlignment is, among those found, the fraction pointing radially. See
        /// DetectCircularObjects for why both are required.</summary>
        private static (double EdgeCoverage, double Alignment) RadialGradientAlignment(Mat sobelX, Mat sobelY, Point2f center, float radius, int samples = 40)
        {
            const double maxAngleDiff = 20.0 * Math.PI / 180.0;
            const double minMagnitude = 30.0;
            const int band = 2; // Hough's radius/center estimate is only ever approximate — search
                                 // a couple of pixels either side of it at each angle for the
                                 // actual edge rather than trusting the estimate pixel-exactly.

            int edgeFound = 0, aligned = 0;
            for (int i = 0; i < samples; i++)
            {
                double angle = 2 * Math.PI * i / samples;
                double cosA = Math.Cos(angle), sinA = Math.Sin(angle);

                double bestMagnitude = 0, bestGx = 0, bestGy = 0;
                for (int d = -band; d <= band; d++)
                {
                    double r = radius + d;
                    int x = (int)Math.Round(center.X + r * cosA);
                    int y = (int)Math.Round(center.Y + r * sinA);
                    if (x < 1 || y < 1 || x >= sobelX.Width - 1 || y >= sobelX.Height - 1) continue;

                    double gx = sobelX.Get<float>(y, x);
                    double gy = sobelY.Get<float>(y, x);
                    double magnitude = Math.Sqrt(gx * gx + gy * gy);
                    if (magnitude > bestMagnitude) { bestMagnitude = magnitude; bestGx = gx; bestGy = gy; }
                }
                if (bestMagnitude < minMagnitude) continue; // no real edge anywhere near this angle
                edgeFound++;

                double gradientAngle = Math.Atan2(bestGy, bestGx);
                double diff = Math.Abs(NormalizeAngle(gradientAngle - angle));
                diff = Math.Min(diff, Math.Abs(diff - Math.PI));
                if (diff <= maxAngleDiff) aligned++;
            }

            double edgeCoverage = edgeFound / (double)samples;
            double alignment = edgeFound == 0 ? 0 : aligned / (double)edgeFound;
            return (edgeCoverage, alignment);
        }

        private static double NormalizeAngle(double angle)
        {
            while (angle > Math.PI) angle -= 2 * Math.PI;
            while (angle < -Math.PI) angle += 2 * Math.PI;
            return angle;
        }

        // ---------------------------------------------------------------
        // Any object — not "is this a rectangle/square", but "is there a
        // coherent object silhouette here at all", then fit points to that
        // silhouette. A single real object (a car, a chair, a box, a sign)
        // usually shows up in a Canny edge map as several disconnected
        // fragments — body panels, wheels, window frames, cushions — so a
        // strict rectangle/circle match on any one fragment misses it
        // entirely. Closing those gaps with a wide morphological close
        // bridges the fragments into one blob whose outer contour traces
        // the object's actual outline, whatever shape it turns out to be;
        // that outline (simplified to a usable vertex count) is what
        // decides where the calibration points go — not an assumed shape.
        // ---------------------------------------------------------------
        private static List<AiSuggestion> DetectGenericObjects(Mat gray, int imageWidth, int imageHeight)
        {
            var found = new List<AiSuggestion>();
            try
            {
                using var blurred = new Mat();
                Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 0);
                using var edges = new Mat();
                Cv2.Canny(blurred, edges, 40, 120);

                using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(9, 9));
                using var closed = new Mat();
                Cv2.MorphologyEx(edges, closed, MorphTypes.Close, kernel, iterations: 2);
                Cv2.Dilate(closed, closed, kernel, iterations: 1);

                Cv2.FindContours(closed, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                double imageArea = imageWidth * (double)imageHeight;
                double minArea = imageArea * 0.006;   // ignore specks and texture noise
                double maxArea = imageArea * 0.75;    // ignore near-whole-frame blobs (background/frame itself)

                foreach (var contour in contours)
                {
                    double area = Cv2.ContourArea(contour);
                    if (area < minArea || area > maxArea) continue;

                    var rect = Cv2.BoundingRect(contour);
                    if (rect.Width < 28 || rect.Height < 28) continue;
                    if (rect.Width > imageWidth * 0.97 && rect.Height > imageHeight * 0.97) continue; // frame border artifact

                    // Solidity — area vs. its own convex hull's area — separates one coherent
                    // object from a sparse scatter of unrelated edges that happened to close
                    // into a loose loop; a real object's silhouette is reasonably solid.
                    var hullPoints = Cv2.ConvexHull(contour);
                    double hullArea = Cv2.ContourArea(hullPoints);
                    double solidity = hullArea > 0 ? area / hullArea : 0;
                    if (solidity < 0.4) continue;

                    var approx = OutlineRefiner.SimplifyToVertexBudget(contour, maxVertices: 14);
                    if (approx.Length < 3) continue;

                    double aspect = rect.Width / (double)rect.Height;
                    string hint = aspect is > 0.85 and < 1.18 ? " (square-ish)"
                        : aspect > 2.2 ? " (wide)"
                        : aspect < 0.45 ? " (tall)"
                        : "";

                    var points = approx.Select(p => new Point(p.X, p.Y)).ToList();

                    found.Add(new AiSuggestion
                    {
                        DetectedLabel = "Object" + hint,
                        Source = "Outline",
                        Kind = ShapeKind.Polygon,
                        PolygonPoints = points,
                        Bounds = new Rect(rect.X, rect.Y, rect.Width, rect.Height),
                        Confidence = Math.Min(0.85, 0.3 + solidity * 0.5),
                    });
                }
            }
            catch
            {
                // Best-effort — contour analysis failing just means fewer suggestions, not a crash.
            }

            return found.OrderByDescending(s => s.Confidence).Take(12).ToList();
        }

        private static List<Point> ApproximateCircle(Point2f center, float radius, int segments)
        {
            var pts = new List<Point>(segments);
            for (int i = 0; i < segments; i++)
            {
                double angle = 2 * Math.PI * i / segments;
                pts.Add(new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
            }
            return pts;
        }

        private static Rect BoundsOf(IReadOnlyList<Point> pts)
        {
            double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
            double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>Drops suggestions whose bounds mostly overlap a higher-confidence one already kept
        /// (e.g. a contour box drawn around a person the HOG detector already found).</summary>
        private static List<AiSuggestion> DeDuplicate(List<AiSuggestion> input)
        {
            var kept = new List<AiSuggestion>();
            foreach (var candidate in input.OrderByDescending(s => s.Confidence))
            {
                bool overlapsKept = kept.Any(k => OverlapRatio(candidate.Bounds, k.Bounds) > 0.65);
                if (!overlapsKept) kept.Add(candidate);
            }
            return kept;
        }

        private static double OverlapRatio(Rect a, Rect b)
        {
            var intersect = Rect.Intersect(a, b);
            if (intersect.IsEmpty) return 0;
            double intersectArea = intersect.Width * intersect.Height;
            double smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
            return smaller <= 0 ? 0 : intersectArea / smaller;
        }
    }
}
