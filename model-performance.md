# Live model measurements

Measured on 22 October 2026 with Camnovo 0.8.6, Realtek 10.0.22000.20256 and an RTX 3080 Ti. Actual 1080p Lenovo camera pipeline, fixed 1/64 s shutter; High strength. Reduced model inputs are upscaled to camera output. Results are short-run diagnostics, not a quality ranking or calibrated glass-to-glass latency.

| Backend | Processing width | Output FPS | Median / P95 / max age (ms) |
|---|---:|---:|---:|
| FastDVDnet | 640 | 29.1 | 208.4/224.7/254.5 |
| ASwin | 192 | 9.2 | 249.4/287.9/317.5 |
| ViDeNN | 640 | 11.4 | 259.5/307.2/336.9 |
| FlowDenoise | 320 | 14.4 | 232.4/281.4/288.3 |
| UDVD | 320 | 9.3 | 317.0/372.9/376.4 |
| ShiftNet Small | 192 | 14.2 | 255.7/299.1/315.5 |
| PVDD sRGB | 320 | 13.2 | 261.3/310.8/336.2 |

All tested post-warm-up observation windows had zero original-frame interleave. Off returned approximately 30 fps after every model. Loading/failure/late fallback remains possible. Larger sizes can exceed the target. These numbers summarize local diagnostic runs; machine-specific raw reports are excluded from this repository. Reproduce measurements with the app's `--model-self-test <report.txt>` option after provisioning the runtime.

Each successful backend measurement sampled frame age 50 times over approximately 3.25 seconds. ViDeNN uses FP32 to avoid FP16 activation overflow on real camera input; its row is from the follow-up run with that correction. The other six rows are from the initial Realtek model run. All seven passed their successful delivery/recovery checks; these measurements are not a worst-case latency guarantee.

ASwin: CC BY-NC 4.0, non-commercial. ShiftNet: standalone upstream license not established; local research evaluation. Neural AMD acceleration is not implemented. Keep Studio open and keep the separate runtime folder beside the app.
