# Bundled ONNX models

These ship with the repository and are copied next to the executable at build time, so face
detection and saliency work with no setup. `PhotoBook.Analysis` discovers them by file name in the
`models` folder beside the app (`OnnxModelSet`), and degrades cleanly to the model-free classical
pipeline for anything missing.

| File | Model | Purpose | Licence | Source |
|---|---|---|---|---|
| `face_detection_yunet_2023mar.onnx` | YuNet (2023-03) | Face detection → `FocusRegion { kind: Face }` | **MIT** (`LICENSE-yunet.txt`), © 2020 Shiqi Yu | [opencv/opencv_zoo](https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet) |
| `u2netp.onnx` | U²-Netp (small) | Salient-object detection → `FocusRegion { kind: Saliency }` | **Apache-2.0** (`LICENSE-u2net.txt`) | [xuebinqin/U-2-Net](https://github.com/xuebinqin/U-2-Net), ONNX build via [rembg](https://github.com/danielgatis/rembg) |

Both licences permit redistribution, which is why these two are committed and the app needs no
model download.

## What is deliberately **not** bundled

**NIMA aesthetic scoring** (`nima-mobilenet.onnx`). No maintained ONNX build of NIMA exists — every
published implementation ships Keras or PyTorch weights, and converting them would add a Python
toolchain plus unclear terms on the AVA-trained weights. The aesthetic component of `QualityScore`
therefore comes from the classical proxy (sharpness, exposure, colourfulness), which is what
[docs/06](../docs/06-image-analysis.md) describes as the fallback.

If you obtain a NIMA ONNX yourself, drop it in this folder as `nima-mobilenet.onnx` and it is picked
up automatically — the analyzer validates that the output is a 10-bin distribution and ignores the
file if it is not. It is git-ignored so a large third-party file is never committed by accident.

## Verified

Loaded and run against real images on 2026-08-04:

- YuNet input `[1,3,640,640]`, twelve stride heads (`cls_/obj_/bbox_/kps_` at 8/16/32) — matches
  `OnnxAnalyzerOptions.FaceInputSize = 640` and the decoder in `YuNetFaceDetector`. Detected the
  face in OpenCV's `lena.jpg` at 0.95 confidence in the correct position.
- U²-Netp input `input.1` `[1,3,320,320]`, seven mask outputs — matches `SaliencyInputSize = 320`;
  the first output is the fused mask the detector reads.
