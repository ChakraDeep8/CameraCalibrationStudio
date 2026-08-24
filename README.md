# Camera Calibration & Image Studio

A Windows desktop app (WPF, .NET 8, OpenCV via OpenCvSharp) for **ROI/zone calibration**
(draw-and-name regions for a computer-vision pipeline), plus lens (chessboard) calibration and
general image editing — all with the ability to grab frames directly from RTSP cameras.

## Workspaces

- **ROI Calibration** (primary workflow) — open an image, draw regions with
  Rectangle/Square/Polygon/Line, name each one, watch the JSON panel update live, save. Includes
  non-destructive Brightness/Contrast/Sharpness and Color Calibration (temperature, saturation,
  exposure, auto white balance) sections, an object list with rename/duplicate/delete, undo/redo,
  zoom/pan, and a `Batch…` dialog to apply the current adjustments to a folder of related images.
- **Image Editor** — open/save/grab, rotate/flip/crop/resize, real-time non-destructive
  brightness/contrast/sharpness, and an exclusive auto-applying filter (None/Grayscale/Invert/
  Blur/Denoise/Edges).
- **Lens Calibration** — classic OpenCV chessboard intrinsics/distortion calibration.

## Coordinate accuracy

ROI geometry is always stored and exported in the **original image's pixel resolution**,
independent of the current zoom/pan or of any preview adjustment (brightness, color, etc.).
Screen/canvas coordinates never leak into the saved JSON.

## JSON export

- **Save Calibration** writes the app's own JSON schema (image metadata, adjustments, and each
  drawn object's original-resolution coordinates).
- **Export Production Zones JSON** is a second, optional export that adapts the same data to a
  normalized-polygon "zones" schema (`device_id`/`area_id`/`zone_id`/`kind`/`polygon` in 0–1
  coordinates) for pipelines that expect that format — check the field mapping fits your consumer
  before relying on it, since zone-schema conventions vary by deployment.

## Requirements
- Windows 10/11 x64
- .NET 8 runtime (or SDK, to build)

## Build & run

```powershell
cd CameraCalibrationStudio
dotnet build -c Release
dotnet run -c Release --project CameraCalibrationStudio
```

## Publish a standalone .exe

```powershell
cd CameraCalibrationStudio
dotnet publish CameraCalibrationStudio -c Release -r win-x64 --self-contained true -o publish
```

## Notes
- RTSP frame grabs use OpenCV's built-in FFmpeg backend directly.
- The "Grab Frame from RTSP" dialog auto-lists cameras saved by the companion RTSP Camera Viewer
  app, if present.
- Lens calibration follows the classic OpenCV chessboard method (`Cv2.CalibrateCamera`).

## Known gaps
- No editable JSON panel (it's read-only/live-generated; the canvas is the source of truth).
- No light/dark/system theme switching (one dark theme).
- Multi-monitor and non-100% DPI scaling have not been exhaustively tested — please report
  anything that looks off.
