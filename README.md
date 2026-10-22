# Camnovo

A new view for your Lenovo webcam.

Camnovo is a native Windows camera control app and Media Foundation camera extension. It adds live image adjustments, orientation controls and optional GPU denoising to the existing **Lenovo T24mv-30 RGB webcam** pipeline. The current app window is named **Lenovo Camera Studio**, version **0.8.8**.

This is a camera-specific prototype. It targets `USB\VID_17EF&PID_483C&MI_00`, preserves the camera's existing identity and leaves the IR interface alone. It uses the Microsoft USB Video base driver, optionally with Realtek's extension. Compatibility with other webcams has not been established.

Version 0.8.7 supports Realtek-first processing and a **Framing** tab with digital zoom and an arrow pad for position. Click or hold an arrow to move; press the centre button to reset position. Native 1080p capture, orientation, RTX/bilateral filtering, seven research backends, framing controls and Off recovery have been verified. Removing Camnovo restores Realtek-only capture. See the [Realtek driver installation and rollback guide](docs/realtek-webcam-driver.md).

## Features

- Automatic live preview with responsive Fit, 100% and 200% zoom.
- Immediate controls: sliders commit on release; toggles take effect immediately.
- Camera brightness, contrast, saturation, sharpness, gamma, hue and white balance, where supported by the camera.
- Automatic/manual exposure with shutter fractions such as **1/64 s**, backlight compensation and camera presets.
- Driver zoom and horizontal/vertical position in **Framing**, when exposed by the Realtek driver.
- Horizontal mirror and upside-down flip.
- Independent **Shadow colour cleanup** toggle and strength slider: reduces colour in deep shadows while preserving YUV brightness detail. Applies on slider release, works with denoising Off, and is bypassed by Hold Original. Real dark colours may fade.
- Native **1920 × 1080 at 30 FPS** video. The synthetic 1440p option has been removed.
- Automatic neural processing size: starts at a live profile within the selected maximum and reduces size when results exceed the latency budget. Smaller sizes lose detail. Turn automatic sizing off to use a fixed size.
- Delivery status confirms actual filtered output and explicitly identifies original video during loading, errors and lateness. Filtering can recover after a prolonged fallback without changing the selected effect.
- Windows light/dark theme, with optional app overrides.
- Optional NVIDIA RTX processing, an original Direct3D 11 bilateral filter, and adapters for seven research video denoisers.

The tested camera does not expose manual sensor ISO or focus through the checked Windows interfaces. There is no software-gain control. The 5 MP sensor does not expose native 1440p or 5 MP video through the tested capture interfaces; see [native video modes](docs/native-video-modes.md).

**Automatic exposure** enables the camera's own exposure algorithm, rather than a shutter-only controller in the app. Noise can change while FPS stays similar; an increase in internal gain is one possible cause, but this camera does not expose enough telemetry to verify its gain/ISO decisions. Turning off frame-rate priority permits a lower FPS in low light; it does not force a slower shutter.

Auto-mode reads on the tested camera retain the last manual shutter value. The preview also supplied no per-frame exposure-time metadata. The app therefore shows **Auto**, rather than presenting that stale value as a live measurement. If capture metadata is available, it shows the reported shutter fraction. Hardware modes and values refresh every 500 ms without changing settings or interrupting slider edits.

Backlight compensation uses the camera's hardware control. Levels 0–2 were accepted and read back in live tests, but produced little visible change in the tested scenes. Use automatic exposure with a subject against a bright background to evaluate it; successful readback does not guarantee a visible improvement. The app does not replace it with a software brightness filter.

## Build and run

Requirements:

- Windows x64 with the supported Lenovo RGB webcam.
- Visual Studio Build Tools with C++ x64/x86 tools and a Windows 11 SDK; see [.vsconfig](.vsconfig).
- .NET 10 SDK to build, and .NET 10 Desktop Runtime to run.
- A Direct3D 11 feature-level-11 GPU for bilateral processing. NVIDIA neural/RTX backends have additional requirements.

```powershell
git clone https://github.com/kwanLeeFrmVi/Camnovo.git
cd Camnovo
.\Build.ps1
.\build\studio\LenovoCameraControls.exe
```

`Build.ps1` compiles the native engine, native camera helper, WPF app and setup helper, and runs native regression checks. It does not install the extension or change camera settings. Keep the entire `build/studio` folder together when moving the app.

For a UI-only rebuild after the native components have been built:

```powershell
.\Build-Studio.ps1 -DotNetPath 'C:\path\to\dotnet.exe'
```

In the app, choose **Camera → Install / update camera processing** to install the extension. Windows administrator approval is required for installation, removal and device restart. Everyday image adjustments do not need elevation. Close other camera apps during installation or restart. **Camera → Remove processing** restores the saved original camera attachment.

Preview starts automatically. If a video-call app cannot share the camera, close Studio. Some capture paths may bypass Device MFT extensions; verify the result in the app you intend to use. Model backends require Studio to remain open for their worker.

## Denoising

| Option | Implementation | Extra setup |
|---|---|---|
| Bilateral GPU | Original edge-preserving D3D11 spatial filter | None beyond the built camera extension |
| NVIDIA RTX | NVIDIA VFX plus a chroma-noise cleanup stage | Separately obtained compatible NVIDIA VFX runtime |
| FastDVDnet | Five-frame neural video denoising | External Python/model runtime |
| ASwin | Six-frame attention model | External runtime; non-commercial model license |
| ViDeNN | Matched conversion of official TensorFlow weights | External runtime and converted checkpoint |
| FlowDenoise | Motion-aligned filtering with RAFT Small | External runtime and flow weights |
| UDVD | Unsupervised video denoising | External runtime |
| ShiftNet Small | Grouped spatial/temporal shift model | External runtime; upstream license unresolved |
| PVDD sRGB | Real-world video denoising | External runtime |

**Model source trees, pretrained weights, Python packages and proprietary NVIDIA DLLs are not included.** Research backends are optional integrations, not ready-to-run model downloads. See [adapter setup and behavior](models/README.md), [model provenance](model-provenance.json) and [model compatibility](models/model-compatibility.md).

The original bilateral filter is designed for AMD, Intel and NVIDIA D3D11 GPUs; physical testing was on an RTX 3080 Ti. Neural inference was tested with NVIDIA CUDA. AMD neural acceleration is not implemented.

Smaller research-model processing widths trade detail for speed before resizing to camera output. The target is preview frame age below 500 ms, not a guarantee under all resolutions or GPU loads. The worker keeps the newest pending request and discards stale results; Off bypasses processing immediately. [Measured model performance](model-performance.md) includes processing widths, output FPS and frame ages. These diagnostics are not calibrated glass-to-glass latency or a real-world image-quality ranking.

### Optional NVIDIA runtime

Setup can import a compatible, separately obtained VFX 1.2.0 runtime from an `NvidiaRuntime` directory beside `CameraSetup.exe`, or from the directory specified by `CAMNOVO_NVIDIA_RUNTIME` in the setup process environment. No NVIDIA files are distributed by this repository.

The current importer expects the Magpie-style `build-manifest.json` containing `files` entries with `path` and `sha256`, the required VFX/NvCV/NPP DLLs, and any `NVIDIA-VFX-Licenses` notices. It checks the DLL hashes against that manifest before copying into protected application storage. The manifest verifies consistency with the supplied package, not publisher authenticity. See `CopyNvidiaRuntime` in [EngineSetup.cs](src/EngineSetup.cs). An arbitrary NVIDIA SDK directory is not a drop-in package for this importer.

## Architecture and validation

The user-mode Device MFT handles optional resizing, asynchronous denoising and orientation. The WPF app owns the live preview and hardware controls. Neural inference runs in an ordinary-user Python process through a local named pipe, outside the protected camera-service process. Source timestamps are preserved and old pending frames are dropped. No network inference is used; the app does not record webcam images.

Native regression checks cover pixel orientation, Media Foundation transforms, queue behavior, GPU filtering and delayed output delivery. The app also supports these diagnostic entry points:

- `--self-test <path.png>` renders the tabs and themes without a camera image.
- `--shadow-self-test <report.txt>` checks immediate shadow controls, Original bypass/restore and live preview recovery, restoring the user's settings afterward.
- `--live-self-test <report.txt>` exercises live controls, preview, processing and recovery, restoring settings afterward.
- `--model-self-test <report.txt>` measures installed research-model delivery, frame age and Off recovery.
- `--exposure-self-test <report.txt>` checks auto exposure, live readback, stale-poll protection and immediate backlight slider delivery, restoring the tested settings afterward.
- `--realtek-self-test <report.txt>` checks the active Realtek/Camnovo attachment, framing arrows, zoom and reset, effect delivery, Off recovery and preview restart, restoring settings afterward.
- `--framing-self-test <report.txt>` checks directional buttons, limits, centring, keyboard navigation and native 1080p capture, restoring framing afterward.
- `--auto-model-self-test <report.txt>` checks all seven neural choices with the existing exposure settings, automatic model sizing and a 1920 px maximum, counting actual filtered/original delivery and testing Off recovery.
- `--denoise-self-test <report.txt>` reproduces late output with fixed full-HD ViDeNN, checks the visible fallback warning, then enables automatic sizing and verifies filtered recovery, live model switching and Off recovery.

Live diagnostics require the camera and installed extension, and temporarily adjust settings. This is an unsigned prototype, not a production driver package or a general webcam installer.

## Sources and licenses

The engine derives from Microsoft's Device MFT sample and includes WIL headers. Licenses have separate scopes; this repository does not apply one blanket license to every component. See [third-party notices and source revisions](THIRD_PARTY_NOTICES.md), [Microsoft Public License](LICENSE-MS-PL.txt), [WIL MIT license](LICENSE-WIL.txt), and the [original denoiser/adapter MIT license](LICENSE-DENOISER-MIT.txt).

Upstream model and runtime terms still apply. ASwin is CC BY-NC 4.0. ShiftNet's standalone upstream license has not been established; its implementation and weights are not redistributed here. Camnovo is an independent project and is not affiliated with Lenovo, Microsoft or NVIDIA.
