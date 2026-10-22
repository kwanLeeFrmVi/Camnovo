# Live model measurements

Measured on 22 October 2026 with the installed Camnovo 0.8.8 engine, Realtek 10.0.22000.20256 and an RTX 3080 Ti. Actual 1080p Lenovo camera pipeline, existing automatic exposure; High strength, automatic processing size with a 1920 px maximum. Reduced model inputs are resized to native camera output. Results are short-run diagnostics, not a quality ranking or calibrated glass-to-glass latency.

| Backend | Processing width | Output FPS | Median / P95 / max age (ms) |
|---|---:|---:|---:|
| FastDVDnet | 640 | 24.8 | 216.5/247.5/260.6 |
| ASwin | 192 | 7.9 | 283.0/348.7/356.3 |
| ViDeNN | 640 | 9.5 | 315.8/377.9/403.4 |
| FlowDenoise | 320 | 12.1 | 271.2/326.0/336.8 |
| UDVD | 320 | 8.1 | 380.9/441.8/457.6 |
| ShiftNet Small | 192 | 12.2 | 316.5/370.2/370.7 |
| PVDD sRGB | 320 | 11.1 | 326.7/374.9/398.7 |

All tested post-warm-up observation windows had zero original-frame interleave. Off returned approximately 30 fps and 71–113 ms median sample age after every model, with no restart. Baseline was 30 FPS / 75.9 ms. Loading/failure/late fallback remains possible. Larger sizes can exceed the target. These numbers summarize local diagnostic runs; machine-specific raw reports are excluded from this repository. Reproduce measurements with the app's `--auto-model-self-test <report.txt>` option after provisioning the runtime.

Each successful backend measurement sampled frame age 50 times over approximately 3.25 seconds. ViDeNN uses FP32 to avoid FP16 activation overflow on real camera input. All seven rows are from one sequential run of the corrected pipeline. These measurements are not a worst-case latency guarantee.

The separate fixed full-HD ViDeNN stress test reproduced late fallback, recovered filtered delivery at 320 px with automatic sizing and one automatic preview reconnect, then switched to FastDVDnet, ShiftNet and ViDeNN successfully. Off returned 30 FPS / 75.9 ms. No service or driver restart was needed. See [denoiser delivery and recovery](docs/denoiser-delivery.md).

ASwin: CC BY-NC 4.0, non-commercial. ShiftNet: standalone upstream license not established; local research evaluation. Neural AMD acceleration is not implemented. Keep Studio open and keep the separate runtime folder beside the app.
