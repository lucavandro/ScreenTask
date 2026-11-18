using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenTask.Core.Capture
{
    /// <summary>Owns a GPU desktop duplication session and a reusable CPU-readable texture.</summary>
    internal sealed class DesktopDuplicationCapture : IDisposable
    {
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDXGIOutputDuplication? _duplication;
        private ID3D11Texture2D? _staging;
        private bool _hasFrame;
        private readonly int _width;
        private readonly int _height;

        public bool DrawCursorSeparately { get; private set; } = true;

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void CopyMemory(IntPtr destination, IntPtr source, nuint length);

        public DesktopDuplicationCapture(string displayName, Size size)
        {
            _width = size.Width;
            _height = size.Height;
            try
            {
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out var adapter).Success; adapterIndex++)
                {
                    using (adapter)
                    {
                        for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
                        {
                            using (output)
                            {
                                var description = output.Description;
                                if (description.DeviceName != displayName) continue;
                                // Rotated displays keep the established GDI path until rotation is supported here.
                                if (description.Rotation != ModeRotation.Identity && description.Rotation != ModeRotation.Unspecified)
                                    throw new NotSupportedException("Rotated display uses GDI capture.");
                                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                    new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
                                    out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
                                _device = device;
                                _context = context;
                                using var output1 = output.QueryInterface<IDXGIOutput1>();
                                _duplication = output1.DuplicateOutput(device);
                                _staging = device.CreateTexture2D(new Texture2DDescription
                                {
                                    Width = (uint)_width, Height = (uint)_height,
                                    MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                                    SampleDescription = new SampleDescription(1, 0),
                                    Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
                                    BindFlags = BindFlags.None
                                });
                                return;
                            }
                        }
                    }
                }
                throw new NotSupportedException("No matching DXGI display output.");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public Bitmap? CaptureFrame()
        {
            if (_duplication == null || _context == null || _staging == null)
                throw new ObjectDisposedException(nameof(DesktopDuplicationCapture));

            var result = _duplication.AcquireNextFrame(_hasFrame ? 0u : 20u, out var info, out var resource);
            // A static desktop need not produce updates. Reuse only this session's last
            // texture, copying into a new owned bitmap for the capture engine.
            if (result.Code != unchecked((int)0x887A0027)) // DXGI_ERROR_WAIT_TIMEOUT
            {
                result.CheckError();
                try
                {
                    using (resource)
                    using (var texture = resource.QueryInterface<ID3D11Texture2D>())
                    {
                        if (texture.Description.Width != _width || texture.Description.Height != _height)
                            throw new InvalidOperationException("Display size changed.");
                        _context.CopyResource(_staging, texture);
                        _hasFrame = true;
                        if (info.LastMouseUpdateTime != 0) DrawCursorSeparately = info.PointerPosition.Visible;
                    }
                }
                finally
                {
                    _duplication.ReleaseFrame().CheckError();
                }
            }
            if (!_hasFrame) return null;

            _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
            try
            {
                return CopyMappedFrame(mapped.DataPointer, checked((int)mapped.RowPitch), _width, _height);
            }

            finally
            {
                _context.Unmap(_staging, 0);
            }
        }

        internal static Bitmap CopyMappedFrame(IntPtr data, int rowPitch, int width, int height)
        {
            int rowBytes = checked(width * 4);
            if (data == IntPtr.Zero || width <= 0 || height <= 0 || rowPitch < rowBytes)
                throw new ArgumentException("Invalid desktop pixel buffer.");
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            try
            {
                var pixels = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    if (rowPitch == rowBytes && pixels.Stride == rowBytes)
                        CopyMemory(pixels.Scan0, data, (nuint)checked(rowBytes * height));
                    else
                        for (int y = 0; y < height; y++)
                            CopyMemory(IntPtr.Add(pixels.Scan0, checked(y * pixels.Stride)), IntPtr.Add(data, checked(y * rowPitch)), (nuint)rowBytes);
                }
                finally
                {
                    bitmap.UnlockBits(pixels);
                }
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _staging?.Dispose(); _staging = null;
            _duplication?.Dispose(); _duplication = null;
            _context?.Dispose(); _context = null;
            _device?.Dispose(); _device = null;
            _hasFrame = false;
        }
    }
}
