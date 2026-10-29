using NAudio.Wave;

namespace Otto.Audio.Boost;

/// <summary>One boosted app as the render thread sees it: its captures and its gain.</summary>
public sealed class BoostSource
{
    private volatile float _gain;
    private volatile float _peak;
    private volatile LoopbackCapture[] _captures = Array.Empty<LoopbackCapture>();

    public float Gain { get => _gain; set => _gain = value; }
    /// <summary>Peak of the boosted signal since the meter last read it (0..1).</summary>
    public float TakePeak() { float p = _peak; _peak = 0f; return Math.Min(1f, p); }
    internal void ReportPeak(float p) { if (p > _peak) _peak = p; }
    public LoopbackCapture[] Captures { get => _captures; set => _captures = value; }
}

/// <summary>
/// Sums every boosted app (× its gain) into Otto's output stream, then a peak limiter keeps the boost
/// from clipping: instant attack, ~150 ms release, ceiling just under full scale.
/// </summary>
public sealed class BoostMixer : ISampleProvider
{
    private const float Ceiling = 0.97f;
    private volatile BoostSource[] _sources = Array.Empty<BoostSource>();
    private readonly float _release;
    private float _limit = 1f;

    public WaveFormat WaveFormat { get; }

    public BoostMixer(WaveFormat floatFormat)
    {
        WaveFormat = floatFormat;
        _release = (float)(1.0 - Math.Exp(-1.0 / (0.15 * floatFormat.SampleRate)));
    }

    public BoostSource[] Sources { get => _sources; set => _sources = value; }

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        int ch = WaveFormat.Channels;
        foreach (var src in _sources)
        {
            float gain = src.Gain;
            float peak = 0f;
            foreach (var cap in src.Captures) peak = Math.Max(peak, cap.Ring.MixInto(buffer, offset, count, gain, ch));
            src.ReportPeak(peak * gain);
        }

        for (int i = offset; i < offset + count; i += ch)
        {
            float framePeak = 0f;
            for (int c = 0; c < ch; c++) framePeak = Math.Max(framePeak, Math.Abs(buffer[i + c]));
            float need = framePeak > Ceiling ? Ceiling / framePeak : 1f;
            _limit = need < _limit ? need : _limit + (1f - _limit) * _release;
            if (_limit > need) _limit = need;
            for (int c = 0; c < ch; c++) buffer[i + c] *= _limit;
        }
        return count; // always full: Otto's stream must never end
    }
}
