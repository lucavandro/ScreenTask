# Video format

ScreenTask streams JPEG frames (`0x01` + image/jpeg) using the existing quality
slider. WebP was removed after the performance evaluation; see
[the measured comparison](WEBP_PERFORMANCE.md).

Old settings containing VideoFormat remain loadable and keep all other settings.
Cached viewers that include the old capability query still receive JPEG.
The snapshot endpoint remains JPEG. GPU capture and the bounded decoding queue
are unchanged.
