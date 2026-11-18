using System.Drawing;
using System.Runtime.InteropServices;
using ScreenTask.Core.Streaming.WebRtc;

namespace ScreenTask.Tests.Core;

public class H264ConversionTests
{
    [Theory]
    [InlineData(0, 0, 0, 16, 128, 128)]
    [InlineData(255, 255, 255, 235, 128, 128)]
    [InlineData(255, 0, 0, 82, 90, 240)]
    [InlineData(0, 255, 0, 144, 54, 34)]
    [InlineData(0, 0, 255, 41, 240, 110)]
    public void Nv12_UsesLimitedRangeBt601_AndPadsOddDimensions(int r, int g, int b, int y, int u, int v)
    {
        using var bitmap = new Bitmap(3, 3);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.FromArgb(r, g, b));
        IntPtr buffer = Marshal.AllocHGlobal(24);
        try
        {
            H264Encoder.CopyNv12(bitmap, buffer, 4, 4);
            var bytes = new byte[24];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            Assert.All(bytes.Take(16), value => Assert.Equal(y, value));
            for (int i = 16; i < bytes.Length; i += 2)
            {
                Assert.Equal(u, bytes[i]);
                Assert.Equal(v, bytes[i + 1]);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
