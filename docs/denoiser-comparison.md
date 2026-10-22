# Choosing a denoiser for detail and noise

The comparison in **Effects → Compare denoisers** ranks tested profiles, including their strength and processing size. It is a local measurement on the RTX 3080 Ti and Lenovo T24mv-30 with Realtek 10.0.22000.20256, not a universal ranking of the research papers.

The measured recommendation is **Bilateral GPU / Low at native 1080p**. It reduced the local-camera high-frequency grain estimate by approximately 55% and retained a 0.92 fine-detail contrast response on the known chart. These are separate, limited measurements, not a promise of 55% improvement in every scene or zero loss of all image detail. It runs at approximately 30 FPS with substantial room below the 500 ms limit.

**Use recommended settings** applies the selected recommendation immediately. Clicking a comparison result applies that result's tested settings. Selecting a model from the ordinary dropdown preserves the usual strength/size controls; it does not imply those controls match the compared profile. Backend identities remain stable when the displayed order changes.

For this detail-first request, a tested profile uses its exact processing size with automatic sizing disabled. The user can enable automatic sizing separately, trading detail for more latency headroom. Shadow colour cleanup is disabled when applying a compared profile so colour suppression does not confound the comparison. Camera exposure, gain and hardware image controls are not modified.

## What was compared

- Nine backends, multiple processing widths and Low/Medium/High strength: native RTX and bilateral; FastDVDnet; ASwin; ViDeNN; FlowDenoise; UDVD; ShiftNet Small; PVDD sRGB.
- The same known fine-detail chart with independent noise in each temporal frame, plus a moving version. Output is evaluated at 1920 × 1080 after the actual processing-size reduction and resizing, rather than comparing small images at different scales.
- Detail score: correlation response of known high-frequency chart detail. A response near one preserves the chart's contrast. This is not a percentage of all perceptual image detail.
- Noise score: residual RMS error in a known uniform chart patch. A flat colour alone cannot win, because the detail and motion components penalise loss of structure.
- Initial profile screening uses 50% fine-detail response, 30% flat-patch chart noise reduction and 20% moving-detail PSNR. Final ordering splits the noise component equally between the known chart and actual-camera grain: 50% detail, 15% chart noise, 15% camera grain, 20% motion. Latency is a separate eligibility gate.
- The qualified profiles were then compared against the same independent local-camera sample, using stable, flat pixels and a separate 48-frame temporal-mean reference. High-pass residual removes slow exposure/lighting drift from the grain estimate. Camera absolute RMSE contains drift and is retained only as a diagnostic, not used as pure-noise benefit. No camera pixels were saved or uploaded.
- The native RTX entry runs the existing app's `VideoSuperRes` restoration modes plus GPU chroma cleanup from the supplied runtime. It is not NVIDIA's separate Webcam Denoise model. The native bridge uses the same classes as the camera engine.

## Latency qualification

Each nominee was run in the actual camera pipeline at its exact fixed size and strength. Qualification requires at least four filtered frames, zero original frames in the post-warm-up observation window, and both P95 and measured maximum source-frame age below 500 ms. Cold model loading is excluded, and the UI continues to identify original-image fallback during warm-up or failure. Original source timestamps are retained.

The initial profile measurement samples age 50 times over roughly 3.25 seconds. The best-ranked finalist receives a longer 180-observation follow-up; the native alternatives are checked again after ranked display order is enabled. Very large models that exceed the limit are excluded at that size, even if offline inference appeared fast enough. Full-HD FlowDenoise took approximately 3.5 seconds per offline result in this test, so its remaining full-HD strengths were not pursued.

Full-HD FastDVDnet initially passed a short window (maximum 481 ms), but its longer follow-up reached 542 ms. That profile is excluded from the under-500-ms ranking. The smaller 1280 px Low profile passed a longer check with P95 327 ms and maximum 346 ms. Its reduced fine-detail response is visible in the ranking. A quality win alone cannot override the user's delay limit.

These are short-run measurements, not a hard real-time guarantee. GPU load, scene movement, exposure and other camera clients can change latency or quality. A result near the limit has less headroom. FPS is shown alongside delay: under 500 ms does not imply smooth 30 FPS motion.

Exact selected profiles and measurements are in [denoiser-ranking.json](../models/denoiser-ranking.json). The built app includes numerical comparison and live verification reports. Lower-resolution neural profiles can remove visible fine detail even when they successfully reduce noise; that is reflected in their ranking.

## Reproducing the comparison

1. Build the regular app and engine, then build `Build-QualityBench.cmd` with the same Visual Studio developer-command path used by `Build-NativeApp.cmd`. `QualityBridge.dll` is an optional diagnostic helper, not an installed camera component.
2. For native RTX comparison, provide the existing private `Nvidia` runtime folder beside that helper, with its original notices. No vendor DLLs or weights are committed to this repository.
3. Close other camera clients and run the runtime Python on `models/compare_denoisers.py --camera <CameraNative.dll> --native <QualityBridge.dll> --models <runtime/models> --output <local-report-directory>`. Use a stable camera scene containing texture and plain areas. The camera effect settings are restored after sampling.
4. Nominate size/strength combinations from the quality results, then use `LenovoCameraControls.exe --ranking-self-test <profiles.json> <live-report.json>` to validate actual delivery and age. Profile records contain Backend, Width, Level and Score. The test restores effect settings when finished.
5. Review the synthetic-chart crops as well as the metrics. Ship only qualified profiles in the ranking manifest; do not promote an offline timing estimate to a live-latency claim.

<!-- measurements -->

## Measured profiles

| Rank | Denoiser | Strength / processing width | FPS | P95 / max age (ms) |
|---|---|---|---:|---:|
| 1 | Bilateral GPU | Low / 1920 | 30.0 | 154 / 159 |
| 2 | NVIDIA RTX | High / 1920 | 30.0 | 154 / 161 |
| 3 | FastDVDnet | Low / 1280 | 12.2 | 327 / 346 |
| 4 | FlowDenoise | Low / 960 | 9.1 | 347 / 372 |
| 5 | ViDeNN | High / 640 | 10.5 | 336 / 354 |
| 6 | PVDD sRGB | High / 640 | 7.0 | 439 / 444 |
| 7 | ShiftNet Small | Low / 320 | 7.0 | 445 / 459 |
| 8 | UDVD | Low / 320 | 8.8 | 389 / 400 |
| 9 | ASwin | High / 192 | 8.6 | 342 / 355 |

Where a profile was repeated, the table keeps the worst observed P95/maximum and the lower FPS. The winning bilateral profile also passed the 180-observation check with 412 filtered frames and zero original frames; that run reached P95 122 ms and maximum 129 ms.
