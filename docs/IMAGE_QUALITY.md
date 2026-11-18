# Image Quality and framerate

The Image Quality slider feeds GDI+'s JPEG `Encoder.Quality` parameter (20–100 in
the UI). It is a lossy quality setting, not a percentage of the original file size.
Lower quality usually discards more detail and reduces encoded frame size.

Smaller frames can relieve encoder and network pressure, but this does not change
capture resolution, capture cost or target FPS. An FPS increase depends on the
screen content and current bottleneck and is not guaranteed. A quality value of
80 does not mean the output is 80% of the original file size.

The slider tooltip explains this distinction. JPEG remains the production codec
after the [WebP performance evaluation](WEBP_PERFORMANCE.md).
