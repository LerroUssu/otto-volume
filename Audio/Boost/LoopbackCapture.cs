using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Otto.Audio.Boost;

/// <summary>
/// Records what one process (and its children) plays, wherever it plays it (Windows 10 2004+ process
/// loopback). Measured on Windows 10 19045: the signal is taken after the app's session volume/mute and
/// before the endpoint volume/mute, so an app sent to a muted output still records at full level.
/// </summary>
public sealed class LoopbackCapture : IDisposable
{
    private readonly uint _pid;
    private readonly WaveFormat _format;
    private readonly FloatRing _ring;
    private readonly Thread _thread;
    private volatile bool _stop;

    public uint Pid => _pid;
    public FloatRing Ring => _ring;
    public string? Error { get; private set; }

    public LoopbackCapture(uint pid, WaveFormat floatFormat)
    {
        _pid = pid;
        _format = floatFormat;
        _ring = new FloatRing(floatFormat.SampleRate * floatFormat.Channels); // 1 s
        _thread = new Thread(Run) { IsBackground = true, Name = $"Otto.Capture.{pid}", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        AudioClient? client = null;
        try
        {
            var flags = AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback
                      | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    client = new AudioClient(Activate(_pid));
                    client.Initialize(AudioClientShareMode.Shared, flags, 20 * 10000, 0, _format, Guid.Empty);
                    break;
                }
                catch (COMException ex) when (attempt < 6 && !_stop)
                {
                    // right after an earlier capture of the same process Windows can refuse for a moment
                    try { client?.Dispose(); } catch { }
                    client = null;
                    Log.Write($"[boost] capture pid {_pid} attempt {attempt}: {ex.Message}");
                    Thread.Sleep(200 * attempt);
                }
            }
            using var ready = new AutoResetEvent(false);
            client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
            var capture = client.AudioCaptureClient;
            int channels = _format.Channels;
            float[] scratch = new float[4096];
            client.Start();
            long total = 0; float peak = 0f; bool reported = false; var started = Environment.TickCount64;
            while (!_stop)
            {
                ready.WaitOne(100);
                int packet;
                while (!_stop && (packet = capture.GetNextPacketSize()) > 0)
                {
                    IntPtr data = capture.GetBuffer(out int frames, out var bufferFlags);
                    int n = frames * channels;
                    if (scratch.Length < n) scratch = new float[n];
                    if ((bufferFlags & AudioClientBufferFlags.Silent) != 0) Array.Clear(scratch, 0, n);
                    else Marshal.Copy(data, scratch, 0, n);
                    capture.ReleaseBuffer(frames);
                    _ring.Write(scratch, n);
                    if (!reported) { total += n; for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(scratch[i])); }
                }
                if (!reported && Environment.TickCount64 - started > 1000)
                {
                    reported = true;
                    Log.Write($"[boost] capture pid {_pid}: {total / channels} frames, peak {peak:0.000000} in the first second");
                }
            }
            client.Stop();
            capture.Dispose();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error($"[boost] capture pid {_pid}", ex);
        }
        finally { try { client?.Dispose(); } catch { } }
    }

    public void Dispose()
    {
        _stop = true;
        if (!_thread.Join(1000)) Log.Write($"[boost] capture pid {_pid} did not stop in time");
    }

    // ---- activation ----------------------------------------------------------------------

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op); }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation { void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface); }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

    private sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly TaskCompletionSource<object> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out int hr, out object o);
                if (hr < 0) Done.TrySetException(Marshal.GetExceptionForHR(hr)!); else Done.TrySetResult(o);
            }
            catch (Exception ex) { Done.TrySetException(ex); }
        }
    }

    /// <summary>An IAudioClient that records <paramref name="pid"/> and its child processes.</summary>
    public static IAudioClient Activate(uint pid)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK, { pid, INCLUDE_TARGET_PROCESS_TREE } } in a VT_BLOB PROPVARIANT
        IntPtr p = Marshal.AllocCoTaskMem(12), pv = Marshal.AllocCoTaskMem(24);
        try
        {
            Marshal.WriteInt32(p, 0, 1);
            Marshal.WriteInt32(p, 4, (int)pid);
            Marshal.WriteInt32(p, 8, 0);
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(pv, i, 0);
            Marshal.WriteInt16(pv, 0, 65 /* VT_BLOB */);
            Marshal.WriteInt32(pv, 8, 12);
            Marshal.WriteIntPtr(pv, 16, p);
            var done = new Completion();
            ActivateAudioInterfaceAsync(@"VAD\Process_Loopback", typeof(IAudioClient).GUID, pv, done, out var op);
            try
            {
                if (!done.Done.Task.Wait(5000)) throw new TimeoutException("process loopback activation");
                return (IAudioClient)done.Done.Task.Result;
            }
            finally
            {
                // The operation holds the client too and reached .NET twice (out parameter + callback): let go of
                // it fully now, or Windows refuses the next capture of this process until a garbage collection.
                Marshal.FinalReleaseComObject(op);
            }
        }
        finally { Marshal.FreeCoTaskMem(p); Marshal.FreeCoTaskMem(pv); }
    }
}

/// <summary>
/// Single-producer/single-consumer float FIFO between a capture thread and the render thread. Keeps
/// latency bounded: a backlog beyond <see cref="MaxBacklog"/> is trimmed (clock drift, a stalled
/// renderer), and output starts only once <see cref="Cushion"/> samples are buffered.
/// </summary>
public sealed class FloatRing
{
    private readonly float[] _buf;
    private int _read, _count;
    private bool _primed;
    private readonly object _gate = new();

    public int Cushion { get; set; }
    public int MaxBacklog { get; set; }

    public FloatRing(int capacity)
    {
        _buf = new float[capacity];
        MaxBacklog = capacity / 2;
    }

    public void Write(float[] src, int n)
    {
        lock (_gate)
        {
            if (n > _buf.Length) { src = src[(n - _buf.Length)..n]; n = _buf.Length; }
            int free = _buf.Length - _count;
            if (n > free) { int drop = n - free; _read = (_read + drop) % _buf.Length; _count -= drop; }
            int write = (_read + _count) % _buf.Length;
            int first = Math.Min(n, _buf.Length - write);
            Array.Copy(src, 0, _buf, write, first);
            Array.Copy(src, first, _buf, 0, n - first);
            _count += n;
        }
    }

    /// <summary>Adds up to <paramref name="n"/> samples × gain into dst; missing samples count as silence. Returns the peak read.</summary>
    public float MixInto(float[] dst, int offset, int n, float gain, int channels)
    {
        lock (_gate)
        {
            if (!_primed) { if (_count < Cushion) return 0f; _primed = true; }
            if (_count > MaxBacklog)
            {
                int drop = _count - Cushion;
                drop -= drop % channels; // keep the channels aligned
                _read = (_read + drop) % _buf.Length; _count -= drop;
            }
            int take = Math.Min(n, _count);
            take -= take % channels;
            float peak = 0f;
            for (int i = 0; i < take; i++)
            {
                float s = _buf[(_read + i) % _buf.Length];
                dst[offset + i] += s * gain;
                float a = Math.Abs(s);
                if (a > peak) peak = a;
            }
            _read = (_read + take) % _buf.Length; _count -= take;
            if (take < n) _primed = false; // ran dry: wait for the cushion again instead of crackling
            return peak;
        }
    }
}
