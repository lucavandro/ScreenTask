using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ScreenTask.Core.Streaming.WebRtc;

/// <summary>Owns one libdatachannel connection and its pinned callback delegates.</summary>
internal sealed class NativeRtcPeer : IDisposable
{
    private const string Library = "ScreenTaskDataChannel";
    private static readonly object RuntimeLock = new();
    private static IntPtr _runtime;
    private static readonly List<IntPtr> RuntimeDependencies = new(); // Kept loaded for the process lifetime.
    private readonly object _sync = new();
    private readonly TaskCompletionSource _gathered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly GatheringCallback _gathering;
    private readonly StateCallback _state;
    private readonly OpenCallback _open;
    private readonly OpenCallback _pli;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly uint _timestampBase = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
    private uint _lastTimestamp;
    private int _connection = -1, _track = -1;
    private volatile bool _disposed;
    private volatile bool _ready;
    private volatile bool _sendingAllowed = true;
    internal bool IsReady => _ready && _sendingAllowed;
    internal void StopSending() => _sendingAllowed = false;
    internal event Action? Ready;
    internal event Action? Failed;
    internal event Action? KeyFrameRequested;

    static NativeRtcPeer() => NativeLibrary.SetDllImportResolver(typeof(NativeRtcPeer).Assembly,
        (name, assembly, path) => name == Library ? LoadRuntime() : IntPtr.Zero);

    internal static void EnsureAvailable() => _ = LoadRuntime();

    private static IntPtr LoadRuntime()
    {
        lock (RuntimeLock)
        {
            if (_runtime != IntPtr.Zero) return _runtime;
            if (!Environment.Is64BitProcess || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("WebRTC requires Windows x64.");
            var bytes = ReadResource("datachannel.dll");
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenTask", "Native", "libdatachannel-0.24.6.1-" + hash[..16]);
            string file = Extract(directory, "datachannel.dll", bytes);
            // Prefer serviced system libraries. Only clean PCs use the app-local CRT fallback.
            try { return _runtime = NativeLibrary.Load(file); }
            catch (DllNotFoundException)
            {
                string[] names = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll"];
                var dependencies = names.Select(ReadResource).ToArray();
                string identity = Convert.ToHexString(SHA256.HashData(dependencies.SelectMany(b => b).ToArray()))[..16];
                string bundled = directory + "-crt-" + identity;
                for (int i = 0; i < names.Length; i++)
                    RuntimeDependencies.Add(NativeLibrary.Load(Extract(bundled, names[i], dependencies[i])));
                return _runtime = NativeLibrary.Load(Extract(bundled, "datachannel.dll", bytes));
            }
        }
    }

    private static byte[] ReadResource(string name)
    {
        using var resource = typeof(NativeRtcPeer).Assembly.GetManifestResourceStream("ScreenTask.Native." + name)
            ?? throw new DllNotFoundException($"Missing {name}; prepare the portable WebRTC runtime before building.");
        using var stream = new MemoryStream();
        resource.CopyTo(stream);
        return stream.ToArray();
    }

    private static string Extract(string directory, string name, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, name);
        if (!File.Exists(file) || !SHA256.HashData(File.ReadAllBytes(file)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
        {
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, file, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return file;
    }

    internal NativeRtcPeer(string address, int port)
    {
        _gathering = (_, state, _) => { if (state == 2) _gathered.TrySetResult(); };
        _state = (_, state, _) =>
        {
            if (state is 3 or 4 or 5)
            {
                _ready = false;
                if (!_disposed) { try { Failed?.Invoke(); } catch { } }
            }
        };
        _open = (_, _) => { if (_disposed) return; _ready = true; try { Ready?.Invoke(); } catch { } };
        _pli = (_, _) => { try { KeyFrameRequested?.Invoke(); } catch { } };
        try
        {
            var config = new Configuration
            {
                BindAddress = address,
                EnableIceUdpMux = true,
                DisableAutoNegotiation = true,
                ForceMediaTransport = true,
                PortRangeBegin = checked((ushort)port),
                PortRangeEnd = checked((ushort)port)
            };
            _connection = Check(rtcCreatePeerConnection(in config));
            Check(rtcSetGatheringStateChangeCallback(_connection, _gathering));
            Check(rtcSetStateChangeCallback(_connection, _state));
            uint ssrc = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)) | 1u;
            var track = new Track
            {
                Direction = 1,
                Codec = 0,
                PayloadType = 102,
                Ssrc = ssrc,
                Mid = "video",
                Name = "ScreenTask",
                Msid = "screentask",
                TrackId = "screen"
            };
            _track = Check(rtcAddTrackEx(_connection, in track));
            var packetizer = new Packetizer
            {
                Ssrc = ssrc,
                Cname = "ScreenTask",
                PayloadType = 102,
                ClockRate = 90000,
                SequenceNumber = BitConverter.ToUInt16(RandomNumberGenerator.GetBytes(2)),
                Timestamp = _timestampBase,
                MaxFragmentSize = 1100,
                NalSeparator = 3
            };
            Check(rtcSetH264Packetizer(_track, in packetizer));
            Check(rtcChainRtcpSrReporter(_track));
            Check(rtcChainRtcpNackResponder(_track, 512));
            Check(rtcChainPliHandler(_track, _pli));
            Check(rtcSetOpenCallback(_track, _open));
        }
        catch { Dispose(); throw; }
    }

    internal async Task<string> CreateOfferAsync(CancellationToken cancellationToken)
    {
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); Check(rtcSetLocalDescription(_connection, "offer")); }
        await _gathered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var buffer = new StringBuilder(65536);
            Check(rtcGetLocalDescription(_connection, buffer, buffer.Capacity));
            return buffer.ToString();
        }
    }

    internal void SetAnswer(string sdp)
    {
        if (sdp.Length > 65536 || !sdp.StartsWith("v=0", StringComparison.Ordinal) || sdp.Contains("m=application", StringComparison.Ordinal))
            throw new ArgumentException("Invalid video answer.", nameof(sdp));
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); Check(rtcSetRemoteDescription(_connection, sdp, "answer")); }
    }

    internal bool Send(byte[] frame)
    {
        lock (_sync)
        {
            if (_disposed || !IsReady || rtcGetBufferedAmount(_track) > 256 * 1024) return false;
            uint elapsed = unchecked((uint)(Stopwatch.GetElapsedTime(_started).TotalSeconds * 90000));
            elapsed = Math.Max(_lastTimestamp + 1, elapsed);
            _lastTimestamp = elapsed;
            Check(rtcSetTrackRtpTimestamp(_track, unchecked(_timestampBase + elapsed)));
            return rtcSendMessage(_track, frame, frame.Length) >= 0;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _ready = false;
            _gathered.TrySetCanceled();
            if (_connection >= 0) rtcClosePeerConnection(_connection);
            if (_track >= 0) rtcDeleteTrack(_track);
            if (_connection >= 0) rtcDeletePeerConnection(_connection);
            _connection = _track = -1;
            GC.KeepAlive(_gathering); GC.KeepAlive(_state); GC.KeepAlive(_open); GC.KeepAlive(_pli);
        }
    }

    private static int Check(int result) => result < 0 ? throw new InvalidOperationException($"WebRTC native operation failed ({result}).") : result;

    [StructLayout(LayoutKind.Sequential)]
    private struct Configuration
    {
        public IntPtr IceServers; public int IceServersCount; public IntPtr Proxy;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? BindAddress;
        public int CertificateType, TransportPolicy;
        [MarshalAs(UnmanagedType.I1)] public bool EnableIceTcp;
        [MarshalAs(UnmanagedType.I1)] public bool EnableIceUdpMux;
        [MarshalAs(UnmanagedType.I1)] public bool DisableAutoNegotiation;
        [MarshalAs(UnmanagedType.I1)] public bool ForceMediaTransport;
        public ushort PortRangeBegin, PortRangeEnd; public int Mtu, MaxMessageSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Track
    {
        public int Direction, Codec, PayloadType; public uint Ssrc;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Mid;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Name;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Msid;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? TrackId;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Profile;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Packetizer
    {
        public uint Ssrc;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Cname;
        public byte PayloadType; public uint ClockRate; public ushort SequenceNumber; public uint Timestamp;
        public ushort MaxFragmentSize; public int NalSeparator, ObuPacketization;
        public byte PlayoutDelayId; public ushort PlayoutDelayMin, PlayoutDelayMax;
        public byte ColorSpaceId, ColorChromaHorz, ColorChromaVert, ColorRange, ColorPrimaries, ColorTransfer, ColorMatrix;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GatheringCallback(int pc, int state, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StateCallback(int pc, int state, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void OpenCallback(int id, IntPtr user);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rtcCreatePeerConnection(in Configuration config);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rtcClosePeerConnection(int pc);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rtcDeletePeerConnection(int pc);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rtcDeleteTrack(int track);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rtcSetGatheringStateChangeCallback(int pc, GatheringCallback callback);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rtcSetStateChangeCallback(int pc, StateCallback callback);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rtcSetLocalDescription(int pc, [MarshalAs(UnmanagedType.LPUTF8Str)] string type);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private static extern int rtcGetLocalDescription(int pc, StringBuilder buffer, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rtcSetRemoteDescription(int pc, [MarshalAs(UnmanagedType.LPUTF8Str)] string sdp, [MarshalAs(UnmanagedType.LPUTF8Str)] string type);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rtcAddTrackEx(int pc, in Track track);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rtcSetH264Packetizer(int track, in Packetizer packetizer);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rtcSetOpenCallback(int track, OpenCallback callback);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rtcChainPliHandler(int track, OpenCallback callback);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rtcChainRtcpSrReporter(int track);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rtcChainRtcpNackResponder(int track, uint count);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rtcSetTrackRtpTimestamp(int track, uint timestamp);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rtcGetBufferedAmount(int track);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int rtcSendMessage(int track, byte[] data, int size);
}
