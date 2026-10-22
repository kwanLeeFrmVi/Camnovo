# Native webcam video resolution

Checked on the connected Lenovo T24mv-30 RGB webcam on 22 October 2026, with Realtek 10.0.22000.20256 and Camnovo processing active.

The physical DirectShow capture filter (IAMStreamConfig, output pin 0) exposes exactly two video modes:

| Resolution | Frame rate | Format |
|---|---|---|
| 1920 × 1080 | 30 FPS | YUY2 |
| 1920 × 1080 | 30 FPS | NV12 |

Media Foundation's source reader exposes 1920 × 1080, 30 FPS, NV12 with the current processing chain. No 2560 × 1440 or 2592 × 1944 video mode was offered by either interface. The physical-filter check avoids mistaking Camnovo's synthetic output type for a native device capability.

Lenovo lists a 2592 × 1944, 5 MP camera but specifies **1080p@30fps** for video in its [official specifications](https://psref.lenovo.com/syspool/Sys/PDF/ThinkVision/ThinkVision_T24mv_30_Monitor/ThinkVision_T24mv_30_Monitor_Spec.pdf). Sensor pixel count and supported USB video output are different capabilities.

Camnovo's former 2560 × 1440 option resized a 1920 × 1080 frame. It could not deliver additional captured detail. Version 0.8.7 removes that option and clears the saved upscale preference. It requests native 1080p capture.

The present driver interfaces provide no native 1440p stream to select. Supporting one would require a device/firmware/driver path that actually exports it; changing an app's output dimensions cannot enable additional sensor readout. This check does not establish whether undocumented firmware modes exist or whether 5 MP still-photo capture is possible.
