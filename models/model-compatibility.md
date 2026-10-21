# Requested model compatibility

This Lenovo camera supplies processed 1080p video, decoded to NV12. The 1440p option upscales that video. A 5 MP sensor specification does not expose sensor RAW data or a higher native video mode through the tested Windows pipeline.

| Requested method | App status | Reason / implementation |
|---|---|---|
| [FastDVDnet](https://github.com/m-tassano/fastdvdnet) | Selectable | Official five-frame clipped-noise checkpoint; MIT |
| [ASwin](https://github.com/LLindn/ASwin-Video-Denoising) | Selectable | Official real-noise checkpoint; latest output of a six-frame past/current window; CC BY-NC 4.0 non-commercial |
| [ViDeNN](https://github.com/clausmichele/ViDeNN) | Selectable | Official TF weights converted to matching Torch inference, parity verified; MIT |
| [FlowDenoise](https://github.com/AIMZ-GFX/ComfyUI-FlowDenoise) | Selectable adaptation | Upstream warp/masks/blending with official RAFT Small; centre-only computation, noise-aware flow preprocessing; MIT |
| [UDVD](https://github.com/sreyas-mohan/udvd) | Selectable | Official blind-video-net-4 checkpoint and posterior reconstruction; MIT |
| [ShiftNet](https://github.com/dasongli1/Shift-Net) | Selectable research option | Official Small/Ours-s checkpoint; standalone upstream license not found, permission for redistribution/commercial use not established |
| [PVDD](https://github.com/Marinyyt/PVDD) | Selectable | Official sRGB checkpoint and network; RAW branch not used; MIT |
| [EDVR](https://github.com/xinntao/EDVR) | Described, unavailable | No compatible denoising checkpoint configured here; published restoration/SR/deblur weights are not presented as a working denoiser |
| [RViDeNet](https://github.com/cao-cong/RViDeNet) | Described, incompatible input | RAW Bayer input and sensor-specific normalization; not available from this webcam pipeline |
| [EMVD](https://github.com/Baymax-chen/EMVD) | Described, incompatible input | RAW pipeline; linked repository is an unofficial implementation |
| [Improved EMVD](https://gretsi.fr/data/colloque/pdf/2022_zheng966.pdf) | Described, incompatible input | RAW denoising method; no compatible processed-video profile configured |
| [Starlight](https://github.com/monakhova/starlight_denoising) | Described, incompatible input | Special RAW RGB+NIR sensor/capture format unavailable here |
| [BRVE](https://github.com/zhanggengchen/BRVE) | Described, incompatible input | Low-light RAW enhancement; sensor RAW is unavailable here |
| [RViDeformer](https://github.com/cao-cong/RViDeformer) | Described, incompatible input | RAW denoising with ISP processing; sensor RAW is unavailable here |

All selectable research models run locally with installed weights. A model name is not proof of real-world noise removal: training data/noise assumptions differ from this camera's colour grain and compression. Use Hold original for comparison. Downscaling itself reduces noise and detail; the synthetic tests separately show that the actual neural/flow output differs from unfiltered input at the same processing size.

The requested 500 ms target is evaluated using the real preview's frame-age diagnostic, including capture, temporal context and processing. It is not inference time alone, and is not a calibrated glass-to-glass measurement. ASwin and ShiftNet need very low processing widths on this PC. Users can choose larger widths, but late results are bypassed. See the live verification report for actual tested profiles rather than assuming every resolution/model combination qualifies.

Model adapters currently accelerate through NVIDIA CUDA. The separate original MIT D3D11 Bilateral filter supports AMD/Intel/NVIDIA in its API design, physically tested here on RTX 3080 Ti. No AMD neural denoising acceleration has been implemented or claimed.
