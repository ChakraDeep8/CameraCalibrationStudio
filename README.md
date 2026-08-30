<div align="center">

# 🎯 Camera Calibration & Image Studio

**Draw the zones. Get the JSON. Ship the pipeline.**

A Windows desktop tool for turning a camera frame into production-ready ROI/zone
calibration — with pixel-exact coordinates, live JSON, and frames pulled straight
from your cameras.

[![Release](https://img.shields.io/github/v/release/ChakraDeep8/CameraCalibrationStudio?style=for-the-badge&color=4C8DFF&labelColor=1C1C22)](https://github.com/ChakraDeep8/CameraCalibrationStudio/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/ChakraDeep8/CameraCalibrationStudio/total?style=for-the-badge&color=4C8DFF&labelColor=1C1C22)](https://github.com/ChakraDeep8/CameraCalibrationStudio/releases)
[![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011%20x64-4C8DFF?style=for-the-badge&labelColor=1C1C22)](#-requirements)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&labelColor=1C1C22)](https://dotnet.microsoft.com/)
[![OpenCV](https://img.shields.io/badge/OpenCvSharp-4.10-5C3EE8?style=for-the-badge&labelColor=1C1C22)](https://github.com/shimat/opencvsharp)

[**⬇️ Download**](https://github.com/ChakraDeep8/CameraCalibrationStudio/releases/latest) ·
[**What's New**](#-whats-new-in-v120) ·
[**Quick Start**](#-quick-start) ·
[**Gestures**](#-canvas-gestures)

</div>

---

## ✨ What's New in v1.2.0

> The release that stopped making you hunt for a good frame — and stopped the canvas eating your image.

### 🎬 Grab Frame from Video — with an automatic blur check

Point it at a video file or stream and it finds a frame worth calibrating on. No scrubbing,
no guessing.

- **Sharpness scoring** via Laplacian variance — a Laplacian isolates edge detail, so a crisp
  frame produces a wide spread of responses and a blurred or motion-smeared one produces a
  narrow one.
- **Candidates are decoded concurrently, not one at a time.** Several workers each open their
  own capture and seek to positions spread across the video; the first frame over the threshold
  cancels the rest. Typically back in about a second.
- **It refuses to hand you a soft frame.** If nothing clears the bar, you're told how many were
  checked and what the best score was — instead of silently calibrating on mush.

```
sharp frame   ████████████████████████  577
blurred copy  ▌                           7.9      threshold ─── 100
```

### 🎨 Floating draw toolbar

The draw tools left the sidebar and now float over the canvas, OneNote-style — icon-only with
tooltips, accent highlight on the active tool, and **draggable** by its grip. The canvas gets
the space back.

### 🏷️ Class assignment in one box and one tick

No more custom-name detour or create-class sub-form. Type the name, decide whether it's
reusable:

| | |
|---|---|
| ☑ **Add as new class** | saved to the class library for every future calibration |
| ☐ **Leave unticked** | names this one region only, library untouched |

### 🔎 The JSON follows you

Select a zone and the Calibration JSON panel **scrolls to that zone's block and highlights it**
— with the numbers ticking over live while you drag its points.

### 📐 Reshape without redrawing

With a polygon selected, **double-click its outline** to insert a vertex right there, then drag
it like any other handle.

### 🐛 Fixed

- **The canvas cropped the bottom of every image.** The content grid was sized to the image's
  full pixel dimensions inside a shorter container, so WPF applied a *layout clip* before the
  zoom transform ever ran — no zoom level could recover the lost rows. Now hosted in a `Canvas`,
  which never layout-clips.
- **Clicking a region grabbed and dragged it.** A click now only selects; `Ctrl`+drag moves.
  Both edit paths capture the mouse, so a region can't get stuck to your cursor any more.
- **The objects list shoved the sidebar off-screen.** It has its own fixed-height scroll panel
  with a sticky filter header.
- Draw/Class panel decluttered — class chips and the always-on instruction caption are gone.

---

## 🗺️ The workflow

```mermaid
flowchart LR
    A["📷 Frame<br/>image · RTSP · video"] --> B["✏️ Draw<br/>rect · square · polygon · line"]
    B --> C["🏷️ Classify<br/>reusable class library"]
    C --> D["🎚️ Tune<br/>preview only, never coordinates"]
    D --> E["📄 Export<br/>native JSON or zones schema"]

    style A fill:#232329,stroke:#4C8DFF,color:#F1F1F4
    style B fill:#232329,stroke:#4C8DFF,color:#F1F1F4
    style C fill:#232329,stroke:#4C8DFF,color:#F1F1F4
    style D fill:#232329,stroke:#4C8DFF,color:#F1F1F4
    style E fill:#232329,stroke:#4C8DFF,color:#F1F1F4
```

---

## 🧰 Workspaces

<table>
<tr>
<td width="33%" valign="top">

### 🎯 ROI Calibration
*The main event*

Draw regions, assign classes, watch the JSON build itself. Non-destructive
brightness/contrast/sharpness, colour calibration, a 17-filter gallery, object list with
visibility/rename/duplicate/delete, undo/redo, zoom/pan, and a `Batch…` dialog to apply the
current adjustments across a folder.

</td>
<td width="33%" valign="top">

### 🖼️ Image Editor
*Fix the frame first*

Rotate, flip, crop, resize. Real-time non-destructive brightness/contrast/sharpness plus a
one-at-a-time filter gallery sharing the same pipeline as ROI Calibration.

</td>
<td width="33%" valign="top">

### 🔬 Lens Calibration
*Undo the optics*

Classic OpenCV chessboard intrinsics and distortion calibration via `Cv2.CalibrateCamera`.

</td>
</tr>
</table>

---

## 📌 Coordinate accuracy — the non-negotiable

> ROI geometry is stored and exported in the **original image's pixel resolution**, always.

Zoom, pan, brightness, colour, filters — none of it touches the numbers. All geometry lives in
image-pixel space and a single transform maps it to the screen, so WPF's own transform-aware hit
testing hands back image coordinates directly. **Screen coordinates never leak into your JSON.**

---

## 📤 Two exports

| Export | What it is |
|---|---|
| **Save Calibration** | The app's native schema — image metadata, adjustment values, and every object's original-resolution coordinates. |
| **Export Production Zones JSON** | Adapts the same data to a normalized-polygon *zones* schema (`device_id` / `area_id` / `zone_id` / `kind` / `polygon` in 0–1 coordinates). |

> ⚠️ Zone-schema conventions vary by deployment. Check the field mapping fits your consumer
> before relying on the production export.

---

## 🖱️ Canvas gestures

| Gesture | Does |
|---|---|
| **Click** a region | Select it — point handles appear |
| **Drag** a handle | Reshape it |
| **Double-click** a polygon edge | Insert a new vertex there |
| `Ctrl` + **drag** inside a region | Move the whole region |
| **Right-click** mid-drag | Fix it in place immediately |
| `Delete` | Remove the selected region |
| **Wheel** | Zoom · **Space**+drag or Pan tool — pan |
| `F` / `1` | Fit to window / 100% |
| `Ctrl`+`Z` / `Ctrl`+`Y` | Undo / redo |
| `Ctrl`+`O` / `Ctrl`+`S` | Open image / save calibration |

*Drawing a polygon:* click to add points · `Enter` or double-click to finish · `Backspace`
removes the last point · `Esc` cancels.

---

## 🚀 Quick Start

**Just want to use it?** Grab the
[latest release](https://github.com/ChakraDeep8/CameraCalibrationStudio/releases/latest),
extract anywhere, run `CameraCalibrationStudio.exe`. Self-contained — **no .NET install needed**.

> 💡 If Windows Smart App Control blocks it on first launch, wait a minute and retry. New
> unsigned binaries are sometimes held briefly while Windows evaluates them.

**Building from source:**

```powershell
git clone https://github.com/ChakraDeep8/CameraCalibrationStudio.git
cd CameraCalibrationStudio
dotnet run -c Release --project CameraCalibrationStudio
```

**Publishing a standalone build:**

```powershell
dotnet publish CameraCalibrationStudio -c Release -r win-x64 --self-contained true -o publish
```

---

## 📋 Requirements

- Windows 10/11 x64
- .NET 8 runtime — *only if building from source; release builds are self-contained*

---

## 📝 Notes

- RTSP and video frame grabs use OpenCV's built-in FFmpeg backend directly — no external
  FFmpeg install.
- **Grab Frame from RTSP** auto-lists cameras saved by the companion
  [RTSP Camera Viewer](https://github.com/ChakraDeep8/RtspCameraViewer), and can save new URLs
  back to that shared list.
- The class library persists per-machine under `%AppData%\CameraCalibrationStudio`, separate
  from any single calibration file — so classes follow you across cameras and projects.

## 🧭 Known gaps

- The JSON panel is read-only and live-generated; the canvas is the source of truth.
- One dark theme — no light/system switching.
- Multi-monitor and non-100% DPI scaling aren't exhaustively tested. Please
  [open an issue](https://github.com/ChakraDeep8/CameraCalibrationStudio/issues) if something
  looks off.

---

<div align="center">
<sub>Built with WPF · .NET 8 · OpenCvSharp</sub>
</div>
