# When denoising shows the original image

Selecting a model requests an effect; it does not prove the model's output reaches the camera. Camnovo 0.8.8 reports actual delivery above the model options:

- **Denoising active:** filtered frames are reaching the output. Hold Original to compare.
- **Loading · original image shown:** the model has not produced a usable result yet.
- **Denoising intermittent:** some original frames are being shown.
- **Showing original · model too slow:** results exceed the live latency budget.
- **Showing original · model failed:** inference failed. Select Off, then a strength to retry; details are available in the model-status tooltip.
- **Showing original · camera delay too high:** frames are already delayed before inference. Close other camera apps, then use Restart camera to reload Windows camera processing. Windows requires administrator approval for this service operation.

**Automatically adjust processing size** is enabled by default. The size selector becomes a maximum. The worker starts at a conservative live profile and reduces its width after repeated late results, excluding the first cold inference. It never exceeds the selected maximum; actual width is shown below. Automatic mode does not change the camera's native output resolution or shutter settings. Disable it for a fixed processing size. Re-selecting a strength, model or size resets the sizing decision.

Smaller model inputs lose detail. Heavy models do not become full-resolution, 30 FPS denoisers simply because the final output is 1080p. The 500 ms frame-age limit remains enforced, and original video is still used during loading, failure or excessive delay.

## Recovery correction

The engine formerly could continue original video indefinitely after prolonged late processing. Original output advanced the last delivered timestamp; a subsequent timely temporal result was still older than that timestamp and was discarded. The engine now pauses original delivery briefly when a valid result establishes recovery, allowing a subsequent filtered frame to overtake the last original timestamp. It does not replay or retimestamp the older result and does not relax the age limit. Off still returns fresh input immediately.

`DeliveryTests` covers slow inference followed by fast centred temporal inference, monotonic timestamps, bounded capture calls and immediate Off. The Python sizing-policy checks run without models; live delivery/recovery checks require the camera and installed engine. See the commands in [README](../README.md).
