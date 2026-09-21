namespace CameraCalibrationStudio.Models.Roi
{
    /// <summary>
    /// Which Magic detectors to run. YOLOv8 is the primary, on-by-default detector — a real
    /// trained classifier (80 COCO categories: person, car, chair, bottle, ...), far more
    /// reliable than the classical heuristics below it. Circular defaults off: on a cluttered
    /// real photo, rounded silhouettes (chair backs, cable loops) can form a near-complete,
    /// radially-consistent rim and pass even a fairly strict Hough-circle verification, so it
    /// stays opt-in. The generic any-object outline detector also defaults off now that YOLO
    /// covers most real-world cases — it's a fallback for objects outside YOLO's 80-class
    /// vocabulary (custom fixtures, signage, etc.), not something you need running alongside it
    /// every time.
    /// </summary>
    public class AiDetectionOptions
    {
        /// <summary>Real object classification via the bundled YOLOv8s ONNX model. Falls back to
        /// the classic HOG pedestrian detector if the model file is missing. See
        /// YoloObjectDetector.</summary>
        public bool UseYolo { get; set; } = true;

        /// <summary>Trace each detection's actual silhouette inside its box and propose a polygon
        /// instead of a plain rectangle. Safe to leave on: a refinement that fails its sanity
        /// checks falls back to the rectangle per box. See OutlineRefiner.</summary>
        public bool RefineOutlines { get; set; } = true;

        public bool Circular { get; set; } = false;

        /// <summary>General object-silhouette detection (any shape, no class name — a fallback
        /// for objects outside YOLO's vocabulary). See AiCalibrationService.DetectGenericObjects.</summary>
        public bool AnyObject { get; set; } = false;

        public static AiDetectionOptions Default => new();
    }
}
