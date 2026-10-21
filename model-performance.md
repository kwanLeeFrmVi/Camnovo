# Live model measurements

RTX 3080 Ti; actual 1080p Lenovo camera pipeline, fixed 1/64 s shutter; High strength. Reduced model inputs are upscaled to camera output. Results are short-run diagnostics, not a quality ranking or calibrated glass-to-glass latency.

| Backend | Processing width | Output FPS | Median / P95 / max age (ms) |
|---|---:|---:|---:|
| FastDVDnet | 640 | 26.6 | 256.5/290.9/298.4 |
| ASwin | 192 | 8.9 | 300.6/354.4/364.1 |
| ViDeNN | 640 | 15.1 | 265.8/298.2/325.3 |
| FlowDenoise | 320 | 11.7 | 299.4/345.8/358.9 |
| UDVD | 320 | 9.5 | 369.1/427.0/459.6 |
| ShiftNet Small | 192 | 13.8 | 313.5/359.4/375.7 |
| PVDD sRGB | 320 | 11.6 | 335.2/391.9/410.3 |

All tested post-warm-up observation windows had zero original-frame interleave. Off returned approximately 30 fps after every model. Loading/failure/late fallback remains possible. Larger sizes can exceed the target. These numbers summarize local diagnostic runs; machine-specific raw reports are excluded from this repository. Reproduce measurements with the app's `--model-self-test <report.txt>` option after provisioning the runtime.

ASwin: CC BY-NC 4.0, non-commercial. ShiftNet: standalone upstream license not established; local research evaluation. Neural AMD acceleration is not implemented. Keep Studio open and keep the separate runtime folder beside the app.
