# Third-party notices and source provenance

Camnovo contains code with different license scopes. Publishing this source does not change the licenses of upstream code, models, or dependencies.

| Component | Source / revision | License |
|---|---|---|
| Device MFT sample-derived engine | [Microsoft Windows-driver-samples](https://github.com/microsoft/Windows-driver-samples/tree/bb68ed5e770bce04dc5395ec9258dafab034184d/avstream/sampledevicemft), `bb68ed5e770bce04dc5395ec9258dafab034184d` | [Microsoft Public License](LICENSE-MS-PL.txt); retain source notices |
| Vendored WIL headers | [microsoft/wil](https://github.com/microsoft/wil/tree/f6f3069ab39fb19c1f89fa5f2d3aa8a1118ace43), `f6f3069ab39fb19c1f89fa5f2d3aa8a1118ace43` | [MIT](vendor/wil/LICENSE.txt) |
| Original bilateral denoiser, asynchronous worker, external-model bridge and original Python adapters/tests | Scope listed in [LICENSE-DENOISER-MIT.txt](LICENSE-DENOISER-MIT.txt) | MIT |

Other original app, setup and utility files have no additional blanket license grant in this repository. Refer to individual source notices and the licenses above for applicable permissions.

## Optional dependencies excluded from the repository

- NVIDIA VFX, NvCV, NPP and other proprietary DLLs: separately obtained runtime, subject to its own terms. The prototype inspected [Magpie revision 3841698348bfb246623d4acf791984c8b68a577b](https://github.com/SAOG0721/Magpie/tree/3841698348bfb246623d4acf791984c8b68a577b). Magpie source and NVIDIA runtime binaries are not bundled.
- Optional Realtek webcam driver and Device MFT: obtained separately from Microsoft's Update Catalog, subject to the package's own terms. No Realtek driver binaries are bundled. See the [installation guide](docs/realtek-webcam-driver.md).
- Python, PyTorch, torchvision, OpenCV, TensorFlow and other model dependencies: installed separately with their own notices.
- Pretrained model implementations and weights: excluded. [model-provenance.json](model-provenance.json) records the versions, origins, hashes and known license notes used for local evaluation. Adapter licensing does not grant permission to upstream implementations or weights.
- ASwin: CC BY-NC 4.0; non-commercial restrictions apply.
- ShiftNet: no standalone upstream license established; do not infer redistribution or commercial permission from its adapter's availability.

The adapters may require modifications or additional dependencies when building a runtime from upstream repositories. This source publication includes no automated model installer or bundled model redistribution.
