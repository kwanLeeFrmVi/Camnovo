# Realtek driver for the T24mv-30 RGB webcam

This guide applies to the RGB webcam with hardware ID `USB\VID_17EF&PID_483C&MI_00` on Windows x64. The tested driver is **Realtek 10.0.22000.20256**, dated **26 September 2022**, from Microsoft's Update Catalog. It is an alternate camera package, rather than a sensor firmware image or a newer version of Microsoft's USB Video driver.

[Official Microsoft package details](https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid=de414c68-13b7-4fd0-bfda-5df813013b08) · [Catalog search](https://www.catalog.update.microsoft.com/Search.aspx?q=VID_17EF%26PID_483C)

The package's `RtLeBA.inf` explicitly lists this RGB hardware ID. Its catalog is signed by Microsoft Windows Hardware Compatibility Publisher. It uses Microsoft's USB Video base driver and adds Realtek's camera-processing extension.

## What the driver added in our test

| Control | Microsoft USB Video | Realtek package |
|---|---|---|
| Pan / horizontal position | Unavailable | −16 to +16, step 1 |
| Tilt / vertical position | Unavailable | −16 to +16, step 1 |
| Zoom | Unavailable | 100–400, step 10; displayed as 1.0–4.0× |
| Manual shutter | 1/4096–1/8 s | Same range |
| Automatic exposure | Available | Available |
| Manual gain / ISO | Unavailable in tested interfaces | Still unavailable |
| Per-frame shutter metadata | Unavailable in tested preview | Still unavailable |

Pan/tilt/zoom are framing controls; this driver does not add optical zoom or a motor to the fixed camera. We verified the added property ranges and shutter set/readback on the real webcam. The combined two-stage processing path is a separate integration check.

## Before installing

Close Camnovo and other camera apps. Keep a backup of the current driver information, camera-interface settings and Camnovo's processing attachment. A full rollback needs the attachment as well as the Windows driver: our test found that switching the driver alone could leave Realtek's processing registration on the camera.

Microsoft's `usbvideo.inf` is an inbox Windows component and normally remains available. `pnputil /export-driver` is for OEM packages, so it does not export that inbox package like an `oemNN.inf` driver. Preserve any existing OEM package separately if replacing one. Do not put user-specific registry exports or backup folders into a public repository.

## Install the matching camera driver

1. Open the official Microsoft package details above. Confirm **AMD64 / Windows x64**, version **10.0.22000.20256**, and the RGB hardware ID shown above.
2. Download its CAB and extract it to a new folder. Windows includes a CAB extraction tool:

   ```powershell
   New-Item -ItemType Directory -Path .\RealtekWebcam -Force
   expand.exe -F:* .\downloaded-driver.cab .\RealtekWebcam
   ```

3. Open **Device Manager → Cameras → Lenovo T24mv-30 RGB Webcam → Update driver → Browse my computer for drivers**. Select the extracted folder containing `RtLeBA.inf`, then follow Windows' installation prompts.
4. Check the camera's **Properties → Driver** page. The expected provider is **Realtek**, version **10.0.22000.20256**. Check **Details → Hardware Ids** if the device name changes.
5. If Windows requests a restart, select **Start → Power → Restart** before testing. A camera-service restart cannot always finish a driver change. Media Foundation can report `MF_E_REBOOT_REQUIRED` until Windows has restarted. Shutting down and turning the PC back on may preserve the previous driver session when Fast Startup is enabled; [Restart performs a full boot cycle](https://learn.microsoft.com/en-us/troubleshoot/windows-client/setup-upgrade-and-drivers/fast-startup-causes-system-hibernation-shutdown-fail).

Only the matching RGB device should be selected for this procedure. The IR interface has suffix `MI_02` and was not targeted in our installation test.

## Use Realtek with Camnovo

Use the updated native app that supports the Realtek chain (version **0.8.6** or later). Open **Camera → Install / update camera processing** and approve the Windows administrator prompt. The installer records Realtek's original attachment and adds Camnovo after it:

```text
Camera → Realtek framing/processing → Camnovo effects → video app
```

Realtek supplies packed YUY2 frames on the tested webcam. Camnovo uses the Windows video processor to convert these to NV12 before filtering, while retaining YUY2 on the connection to Realtek. This lets the RTX, bilateral and model backends use the same input format at native 1080p. The conversion keeps the reported colour range.

The app's **Framing** tab appears when the driver exposes zoom, horizontal position and vertical position. Zoom applies on slider release. Position uses an arrow pad: click for one step, hold for continued movement, or press the centre button to reset position without changing zoom. Arrow keys and Home also work while the pad has focus. **Reset framing** returns zoom and position to the driver's defaults. Preview Fit/100%/200% zoom remains a separate display-only control.

Camnovo checks the active camera's effective processing attachment before enabling its effects. Merely having the Camnovo DLL registered is not enough. Unknown vendor extensions and unsupported/full chains are rejected rather than overwritten.

The installer uses the Windows `CameraDeviceMftCLSIDChain` mechanism and preserves vendor-first order. [Microsoft's Device MFT chaining documentation](https://learn.microsoft.com/en-us/windows-hardware/drivers/stream/dmft-design#configure-the-dmft-chain)

**Verified on 22 October 2026:** 1080p/1440p captures, mirror/flip, all four RTX levels and bilateral filtering passed through the combined chain. Framing controls read back correctly; reset and preview restart passed. Version 0.8.7 additionally verified the arrow pad, limits, keyboard control and native-only output. The earlier 1440p capture check verified an upscale, not extra sensor detail; that option is now removed. See [native modes](native-video-modes.md). Seven research backends delivered filtered 1080p output below the 500 ms P95 frame-age target at their tested processing sizes; see [measurements](../model-performance.md). Off restored approximately 30 FPS. Removing Camnovo restored Realtek-only live capture, and reinstallation restored the pair. The IR interface remained on Microsoft's driver.

The installer reloads the user-mode camera service for processing changes. It does not reset the USB device or reinstall the Realtek driver. Changing the Windows driver itself can still require a full Windows restart.

## Remove Camnovo processing or roll back the driver

- **Keep Realtek, remove Camnovo effects:** choose **Camera → Remove camera processing**. The updated installer restores the saved Realtek-only attachment and keeps the Realtek driver.
- **Return to the full pre-driver setup:** use your pre-installation driver/attachment backup. The backup made during this installation has `Roll-Back-Webcam.cmd`, typed registry-value snapshots, the original driver package, Camnovo component backups and verification reports. Close camera apps before running it and approve Windows elevation.
- Device Manager's **Roll Back Driver** changes the Windows driver when available; it is not a substitute for a saved custom camera-processing attachment. Recheck the provider, live capture and Camnovo's effects after restoration.

No driver or firmware binaries are distributed in the source repository. Download the signed package from Microsoft.
