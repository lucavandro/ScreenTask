using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using ScreenTask.Core.Capture;

namespace ScreenTask.Core.Streaming.WebRtc;

internal sealed record EncodedVideoFrame(byte[] Data, long Time100ns);

/// <summary>Windows H.264 encoder. The owner must serialize encoding and disposal.</summary>
internal sealed class H264Encoder : IDisposable
{
    private readonly IMFTransform _transform;
    private readonly ICodecControl? _codec;
    private readonly IMFMediaEventGenerator? _events;
    private readonly FramePacer? _eventPacer;
    private int _inputRequests;
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _lastTime = -1;
    internal double ConversionMilliseconds { get; private set; }
    internal double WaitMilliseconds { get; private set; }
    internal double SubmitMilliseconds { get; private set; }
    private bool _disposed;
    internal int Width { get; }
    internal int Height { get; }
    internal int Fps { get; }
    internal string Backend => _events != null ? "Windows H.264 (hardware)" : "Windows H.264 (software)";

    internal H264Encoder(int width, int height, int fps, int bitrate = 8_000_000, bool preferHardware = true)
    {
        Width = (width + 1) & ~1;
        Height = (height + 1) & ~1;
        Fps = Math.Clamp(fps, 1, 60);
        MediaFactory.MFStartup().CheckError();
        Exception? last = null;
        foreach (bool hardware in preferHardware ? new[] { true, false } : new[] { false })
        {
            IMFTransform? transform = null;
            ICodecControl? codec = null;
            IMFMediaEventGenerator? events = null;
            try
            {
                if (hardware)
                {
                    using var candidates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, 0x44,
                        new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 },
                        new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 });
                    foreach (var candidate in candidates) { transform = candidate.ActivateObject<IMFTransform>(); break; }
                    if (transform == null) continue;
                    using var attributes = transform.Attributes;
                    attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
                    events = transform.QueryInterface<IMFMediaEventGenerator>();
                }
                else
                {
                    Guid clsid = new("6CA50344-051A-4DED-9779-A43305165E35");
                    Guid iid = new("BF94C121-5B05-4E6F-8000-BA598961414D");
                    Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, IntPtr.Zero, 1, in iid, out var pointer));
                    transform = new IMFTransform(pointer);
                }
                try { codec = (ICodecControl)Marshal.GetObjectForIUnknown(transform.NativePointer); } catch (InvalidCastException) { }
                _codec = codec;
                SetCodec(new("9C27891A-ED7A-40E1-88E8-B22727A024EE"), true);
                SetCodec(new("1C0608E9-370C-4710-8A58-CB6181C42423"), 0u);
                using var output = MediaFactory.MFCreateMediaType();
                Configure(output, VideoFormatGuids.H264);
                output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
                output.Set(MediaTypeAttributeKeys.Mpeg2Profile, 66u);
                transform.SetOutputType(0, output, 0);
                using var input = MediaFactory.MFCreateMediaType();
                Configure(input, VideoFormatGuids.NV12);
                transform.SetInputType(0, input, 0);
                transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
                _transform = transform;
                _events = events;
                if (events != null) _eventPacer = new FramePacer(CancellationToken.None);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                try { using var shutdown = transform?.QueryInterfaceOrNull<IMFShutdown>(); shutdown?.Shutdown(); } catch { }
                events?.Dispose();
                transform?.Dispose();
                if (codec != null) Marshal.ReleaseComObject(codec);
                _codec = null;
            }
        }
        _ = MediaFactory.MFShutdown();
        throw new InvalidOperationException("No usable Windows H.264 encoder.", last);
    }

    private void Configure(IMFMediaType type, Guid subtype)
    {
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, subtype);
        type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)(uint)Width << 32) | (uint)Height);
        type.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)(uint)Fps << 32) | 1u);
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressive
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1ul << 32) | 1u);
    }

    internal void RequestKeyFrame() => SetCodec(new("398C1B98-8353-475A-9EF2-8F265D260345"), true);

    private void SetCodec(Guid key, object value)
    {
        if (_codec != null) _ = _codec.SetValue(in key, ref value);
    }

    internal IReadOnlyList<EncodedVideoFrame> Encode(Bitmap bitmap)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var timer = Stopwatch.StartNew();
        var frames = new List<EncodedVideoFrame>();
        if (_events != null)
        {
            var waiting = Stopwatch.StartNew();
            do
            {
                PumpEvents(frames);
                if (_inputRequests > 0) break;
                _eventPacer?.Wait(TimeSpan.FromMilliseconds(1));
            } while (waiting.ElapsedMilliseconds < 40);
            if (_inputRequests == 0) return frames;
            _inputRequests--;
        }
        WaitMilliseconds = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        using var sample = MediaFactory.MFCreateSample();
        using var buffer = MediaFactory.MFCreateMemoryBuffer(checked(Width * Height * 3 / 2));
        buffer.Lock(out var memory, out _, out _);
        try { CopyNv12(bitmap, memory, Width, Height); }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = Width * Height * 3 / 2;
        sample.AddBuffer(buffer);
        long time = Math.Max(_lastTime + 1, (long)(Stopwatch.GetElapsedTime(_started).TotalSeconds * 10_000_000));
        _lastTime = time;
        sample.SampleTime = time;
        sample.SampleDuration = 10_000_000 / Fps;
        ConversionMilliseconds = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        _transform.ProcessInput(0, sample, 0);
        if (_events != null) PumpEvents(frames);
        else while (ReadOutput() is { } frame) frames.Add(frame);
        SubmitMilliseconds = timer.Elapsed.TotalMilliseconds;
        return frames;
    }

    private void PumpEvents(List<EncodedVideoFrame> frames)
    {
        if (_events == null) return;
        while (true)
        {
            IMFMediaEvent item;
            try { item = _events.GetEvent(1); }
            catch (SharpGen.Runtime.SharpGenException ex) when (ex.ResultCode.Code == unchecked((int)0xC00D3E80)) { return; }
            using (item)
            {
                item.Status.CheckError();
                if (item.EventType == MediaEventTypes.TransformNeedInput) _inputRequests++;
                else if (item.EventType == MediaEventTypes.TransformHaveOutput && ReadOutput() is { } frame) frames.Add(frame);
            }
        }
    }

    private EncodedVideoFrame? ReadOutput(bool allowReconfigure = true)
    {
        var info = _transform.GetOutputStreamInfo(0);
        // Use the native layout so a caller-owned output sample cannot acquire a
        // second managed COM wrapper with ambiguous ownership during marshaling.
        using var provided = ((int)info.Flags & 0x100) == 0 ? MediaFactory.MFCreateSample() : null;
        if (provided != null)
        {
            using var buffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, Width * Height * 3 / 2));
            provided.AddBuffer(buffer);
        }
        var output = new NativeOutput { Sample = provided?.NativePointer ?? IntPtr.Zero };
        var function = Marshal.GetDelegateForFunctionPointer<ProcessOutput>(Marshal.ReadIntPtr(
            Marshal.ReadIntPtr(_transform.NativePointer), 25 * IntPtr.Size));
        int result = function(_transform.NativePointer, 0, 1, ref output, out _);
        if (output.Events != IntPtr.Zero) Marshal.Release(output.Events);
        if (result == unchecked((int)0xC00D6D61) && allowReconfigure)
        {
            if (provided == null && output.Sample != IntPtr.Zero) Marshal.Release(output.Sample);
            using var updated = _transform.GetOutputAvailableType(0, 0);
            _transform.SetOutputType(0, updated, 0);
            return _events != null ? null : ReadOutput(false);
        }
        if (result < 0 && provided == null && output.Sample != IntPtr.Zero) Marshal.Release(output.Sample);
        if (result == unchecked((int)0xC00D6D72)) return null; // MF_E_TRANSFORM_NEED_MORE_INPUT
        Marshal.ThrowExceptionForHR(result);
        using var returned = provided == null && output.Sample != IntPtr.Zero ? new IMFSample(output.Sample) : null;
        var sample = provided ?? returned ?? throw new InvalidOperationException("Encoder returned no sample.");
        using var contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out var data, out _, out int length);
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, length);
            return new(bytes, sample.SampleTime);
        }
        finally { contiguous.Unlock(); }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void CopyNv12(Bitmap bitmap, IntPtr destination, int width, int height)
    {
        int sourceWidth = bitmap.Width, sourceHeight = bitmap.Height;
        var pixels = bitmap.LockBits(new Rectangle(0, 0, sourceWidth, sourceHeight),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* target = (byte*)destination;
            for (int y = 0; y < height; y += 2)
            {
                uint* row0 = (uint*)((byte*)pixels.Scan0 + Math.Min(y, sourceHeight - 1) * pixels.Stride);
                uint* row1 = (uint*)((byte*)pixels.Scan0 + Math.Min(y + 1, sourceHeight - 1) * pixels.Stride);
                for (int x = 0; x < width; x += 2)
                {
                    int x0 = Math.Min(x, sourceWidth - 1), x1 = Math.Min(x + 1, sourceWidth - 1);
                    uint a = row0[x0], b = row0[x1], c = row1[x0], d = row1[x1];
                    target[y * width + x] = Luma(a); target[y * width + x + 1] = Luma(b);
                    target[(y + 1) * width + x] = Luma(c); target[(y + 1) * width + x + 1] = Luma(d);
                    int r = (int)(((a >> 16) & 255) + ((b >> 16) & 255) + ((c >> 16) & 255) + ((d >> 16) & 255)) / 4;
                    int g = (int)(((a >> 8) & 255) + ((b >> 8) & 255) + ((c >> 8) & 255) + ((d >> 8) & 255)) / 4;
                    int blue = (int)((a & 255) + (b & 255) + (c & 255) + (d & 255)) / 4;
                    int uv = width * height + y / 2 * width + x;
                    target[uv] = (byte)(((-38 * r - 74 * g + 112 * blue + 128) >> 8) + 128);
                    target[uv + 1] = (byte)(((112 * r - 94 * g - 18 * blue + 128) >> 8) + 128);
                }
            }
        }
        finally { bitmap.UnlockBits(pixels); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Luma(uint pixel) => (byte)(((66 * (int)((pixel >> 16) & 255) +
        129 * (int)((pixel >> 8) & 255) + 25 * (int)(pixel & 255) + 128) >> 8) + 16);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { using var shutdown = _transform.QueryInterfaceOrNull<IMFShutdown>(); shutdown?.Shutdown(); } catch { }
        _events?.Dispose();
        _eventPacer?.Dispose();
        _transform.Dispose();
        if (_codec != null) Marshal.ReleaseComObject(_codec);
        _ = MediaFactory.MFShutdown();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOutput { public int Stream; public IntPtr Sample; public int Status; public IntPtr Events; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ProcessOutput(IntPtr transform, uint flags, uint count, ref NativeOutput output, out uint status);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    [ComImport, Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecControl
    {
        [PreserveSig] int IsSupported(in Guid key);
        [PreserveSig] int IsModifiable(in Guid key);
        [PreserveSig] int GetParameterRange(in Guid key, out object min, out object max, out object step);
        [PreserveSig] int GetParameterValues(in Guid key, out IntPtr values, out uint count);
        [PreserveSig] int GetDefaultValue(in Guid key, out object value);
        [PreserveSig] int GetValue(in Guid key, out object value);
        [PreserveSig] int SetValue(in Guid key, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}
