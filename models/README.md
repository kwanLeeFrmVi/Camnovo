# Local video denoising adapters

These are inference adapters for seven pretrained backends. `model_adapters.py` loads strict matching official weights; `model_worker.py` connects to the protected native extension through a local named pipe. The app owns the ordinary-user worker. The protected camera service never launches or imports user-writable Python.

Keep a separately provisioned `LenovoDenoiserRuntime` folder next to the app folder. This repository includes adapters, not an automatic runtime installer, upstream model implementations or pretrained weights. The expected layout is `LenovoDenoiserRuntime/python/python.exe` and `LenovoDenoiserRuntime/models/<folder>/...`; each adapter's imports and checkpoint paths in `model_adapters.py` define the remaining per-model layout. The root `model-provenance.json` pins repositories, revisions and weight hashes. Obtain these dependencies from their upstream sources and retain their attribution and license notices.

The tested runtime uses Python 3.11.9, torch 2.5.1+cu124, torchvision 0.20.1+cu124 and numpy 1.26.4. OpenCV, einops, ACSConv and required dependencies are installed privately. TensorFlow 2.15.1 is used for the ViDeNN conversion/parity check, not live inference. NVIDIA CUDA is the tested neural backend. CPU fallback may be too slow; AMD neural acceleration is not implemented. Bilateral GPU is a separate Direct3D 11 implementation.

| ID | Backend | Frames | Default width | Weight/license notes |
|---|---|---:|---:|---|
| 2 | FastDVDnet | 5 | 640 | Official clipped noise; MIT |
| 3 | ASwin | 6 | 192 | Official blind real noise; CC BY-NC 4.0, non-commercial |
| 4 | ViDeNN | 3 | 640 | Official TF checkpoint, matched Torch conversion; MIT |
| 5 | FlowDenoise / RAFT Small | 3 / 5 | 320 | Upstream helpers, official torchvision flow weights; MIT |
| 6 | UDVD | 5 | 320 | Official blind-video-net-4, including posterior; MIT |
| 7 | ShiftNet Small | 5 | 192 | Official Ours-s; standalone upstream license not found |
| 8 | PVDD sRGB | 5 | 320 | Official sRGB checkpoint; MIT |

The model receives genuine consecutive frames. ASwin selects its latest-frame prediction to avoid future-frame delay, which changes the temporal context from offline overlap averaging; other models select the centre. Output retains its original capture timestamp and sample metadata. Smaller widths reduce detail; output is resized to the selected camera size. FastDVDnet/ShiftNet/UDVD use the selected assumed noise sigma (12, 20, 32, 48 / 255), not camera ISO. ASwin/ViDeNN/PVDD use level-dependent output blending; FlowDenoise adjusts masks and blending. ASwin's graph needs at least 128x128 spatial padding.

FlowDenoise computes only the required centre frame, uses official RAFT normalization, pre-smooths only flow inputs and adjusts colour rejection for noisy capture. This is an adaptation, not an unmodified ComfyUI workflow. ViDeNN converts kernels HWIO to OIHW and copies all batch-normalization statistics; `convert_videnn.py` reproduces the conversion and verifies the original TF graph. UDVD exports only the official checkpoint's model tensors. PVDD uses the original sRGB network and dependencies without importing unrelated architectures that require CUDA extensions.

`model_tests.py --models <runtime>/models --report <file> --profiles` tests actual model output on controlled noisy synthetic video: finite output, centre alignment and reduced squared error on flat regions plus an edge. This is not a real-webcam quality benchmark.

Native `ModelBridgeTests.cpp` tests packed 1080p NV12 through each Python model and back, preserving the correct centre timestamp/metadata. App `--model-self-test <report>` separately measures delivered filtered/original counts, live FPS, preview frame age and Off recovery. No webcam images are saved. Inference timing excludes capture/temporal delay and cannot prove the requested 500 ms target.

The native worker keeps one newest request/result. Named-pipe messages have bounded dimensions/lengths and preserve colour matrix/range. The pipe uses a random identifier, rejects remote clients and grants the current user, camera service identities and administrators/system access. No network inference is used.

RViDeNet, EMVD, Improved EMVD, Starlight, BRVE and RViDeformer require RAW sensor data (Starlight includes a special RGB+NIR format). This webcam supplies processed video, not Bayer/RAW metadata. EDVR restoration/SR/deblur models are not a configured denoising solution here. See `model-compatibility.md` for source links.





FlowDenoise Low/Medium/High use the adjacent three-frame window (selected frame has one future neighbour); Ultra uses five frames. The native transport retains five-frame history, and the returned timestamp identifies the actual selected capture. This adaptation reduces GPU work and delay. The original upstream helper functions are retained.

