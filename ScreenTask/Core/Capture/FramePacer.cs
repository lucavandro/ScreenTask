using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScreenTask.Core.Capture
{
    /// <summary>Waits for frame deadlines without changing the system-wide timer resolution or spinning.</summary>
    internal sealed class FramePacer : IDisposable
    {
        private readonly CancellationToken _token;
        private readonly TimerWaitHandle? _timer;
        private readonly WaitHandle[]? _waitHandles;
        private bool _timerFailed;

        public bool IsHighResolution { get; }

        [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, in long dueTime, int period,
            IntPtr completionRoutine, IntPtr argument, IntPtr wakeContext, uint tolerableDelay);

        public FramePacer(CancellationToken token, bool useNativeTimer = true)
        {
            _token = token;
            if (!useNativeTimer) return;
            // TIMER_MODIFY_STATE | SYNCHRONIZE: no named or globally shared timer.
            var handle = CreateWaitableTimerEx(IntPtr.Zero, null, 0x2, 0x00100002);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return; // Older/unsupported systems keep a cancellable standard wait.
            }
            _timer = new TimerWaitHandle(handle);
            _waitHandles = new WaitHandle[] { token.WaitHandle, _timer };
            IsHighResolution = true;
        }

        public bool Wait(TimeSpan remaining)
        {
            if (_token.IsCancellationRequested) return false;
            if (remaining <= TimeSpan.Zero) return true;
            if (_timer != null && _waitHandles != null && !_timerFailed)
            {
                long dueTime = -Math.Max(1, remaining.Ticks); // Relative 100 ns units.
                if (SetWaitableTimerEx(_timer.SafeWaitHandle, in dueTime, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                    return WaitHandle.WaitAny(_waitHandles) == 1;
                _timerFailed = true;
            }
            return !_token.WaitHandle.WaitOne(remaining);
        }

        internal static long NextDeadline(long previous, long now, int fps)
        {
            long interval = (long)Math.Round(Stopwatch.Frequency / (double)Math.Clamp(fps, 1, 60));
            // Drop missed deadlines rather than accumulating a queue of catch-up frames.
            return Math.Max(previous + interval, now);
        }

        public void Dispose() => _timer?.Dispose();

        private sealed class TimerWaitHandle : WaitHandle
        {
            public TimerWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
        }
    }
}
