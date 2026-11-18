using System.Drawing;
using System.Runtime.InteropServices;
using ScreenTask.Core.Capture;

namespace ScreenTask.Tests.Core;

public class DesktopDuplicationTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(20)]
    public void GpuPixels_AreCopiedWithCorrectStrideColorAndOwnership(int pitch)
    {
        var source = new byte[pitch * 2];
        for (int x = 0; x < 3; x++)
        {
            source[x * 4] = 255; // Blue row, unused alpha zero as on some GPU surfaces.
            source[pitch + x * 4 + 2] = 255; // Red row.
        }
        var native = Marshal.AllocHGlobal(source.Length);
        Bitmap frame;
        try
        {
            Marshal.Copy(source, 0, native, source.Length);
            frame = DesktopDuplicationCapture.CopyMappedFrame(native, pitch, 3, 2);
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
        // The bitmap stays valid after unmapping/freeing the GPU-side source.
        using (frame)
        {
            Assert.Equal(Color.FromArgb(255, 0, 0, 255), frame.GetPixel(2, 0));
            Assert.Equal(Color.FromArgb(255, 255, 0, 0), frame.GetPixel(0, 1));
        }
    }

    [Fact]
    public void InvalidGpuStride_IsRejectedBeforeCopying()
    {
        Assert.Throws<ArgumentException>(() => DesktopDuplicationCapture.CopyMappedFrame(new IntPtr(1), 4, 3, 2));
    }

    [Fact]
    public void UnavailableGpu_UsesGdiAndBacksOffBeforeRetrying()
    {
        int attempts = 0;
        using var source = new ScreenCaptureSource(0, (_, _) =>
        {
            attempts++;
            throw new NotSupportedException("Synthetic unavailable GPU");
        });
        using var first = source.CaptureFrame(false);
        using var second = source.CaptureFrame(true);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("GDI", source.CaptureBackend);
        Assert.Equal(1, attempts);
        Assert.Equal(source.Bounds.Size, first.Size);
        source.Dispose();
        using var resumed = source.CaptureFrame(false);
        Assert.NotNull(resumed);
        Assert.Equal(2, attempts);
        Assert.Equal(first.Size, resumed.Size);
    }
}
