using System;
using System.Threading;
using NAudio.Dsp;
using NAudio.Wave;

namespace Island;

// Captures what the speakers are playing (WASAPI loopback) and turns it into B log-spaced levels 0..1.
// Bands is written on the capture thread and read by the UI; a torn read is just one odd frame of bars.
public sealed class Visualizer
{
    public const int B = 16;
    const int M = 10, N = 1 << M;               // 1024-point FFT, ~21 ms at 48 kHz
    // Calibration knobs: floor/range in dB and a high-frequency tilt (music has less energy up top).
    const double FloorDb = -72, RangeDb = 52, TiltDb = 14;

    public readonly float[] Bands = new float[B];
    readonly float[] buf = new float[N];
    readonly Complex[] fft = new Complex[N];
    WasapiLoopbackCapture? cap;
    int fill;
    long lastData;

    public bool Live => Environment.TickCount64 - lastData < 600;

    // Beat (kick) detection: bass energy jumping well above its recent average = onset.
    // Knobs: BeatRatio (sensitivity, lower = more beats) and BeatGapMs (fastest allowed, 200 ms = 300 BPM).
    public double BeatRatio = 1.35;                 // set from Settings (beat sensitivity)
    const double BeatGapMs = 200;

    // Continuous bass (40-160 Hz) and vocal (300-3000 Hz) levels 0..1, auto-gained against their own recent
    // peak so quiet and loud songs both move the wave fully. Peak falls 3 dB/s; level spans 24 dB under it.
    public float Bass, Vocal;
    double bassPeakDb = -70, vocalPeakDb = -70;
    static float AutoGain(double energy, ref double peakDb)
    {
        double db = 10 * Math.Log10(energy + 1e-18);
        peakDb = Math.Max(Math.Max(db, peakDb - 3.0 / 47), -70);
        return (float)Math.Clamp(1 + (db - peakDb) / 24, 0, 1);
    }
    double bassAvg;
    long lastBeat;
    int beats;
    public int Beats => Volatile.Read(ref beats);

    public void Start()
    {
        if (cap != null) return;
        try
        {
            cap = new WasapiLoopbackCapture();
            cap.DataAvailable += OnData;
            cap.RecordingStopped += (_, _) => cap = null;   // device unplugged / changed: restart on next play
            cap.StartRecording();
        }
        catch { cap = null; }
    }

    public void Stop()
    {
        var c = cap;
        cap = null;
        try { c?.StopRecording(); c?.Dispose(); } catch { }
        Array.Clear(Bands);
        Bass = Vocal = 0;
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var c = cap;
        if (c is null) return;
        var fmt = c.WaveFormat;
        int ch = fmt.Channels, bytes = fmt.BitsPerSample / 8, frame = ch * bytes;
        bool isFloat = fmt.BitsPerSample == 32;     // shared-mode mix format is float32 in practice
        for (int i = 0; i + frame <= e.BytesRecorded; i += frame)
        {
            float sum = 0;
            for (int k = 0; k < ch; k++)
                sum += isFloat ? BitConverter.ToSingle(e.Buffer, i + k * 4) : BitConverter.ToInt16(e.Buffer, i + k * bytes) / 32768f;
            buf[fill++] = sum / ch;
            if (fill == N) { Analyze(fmt.SampleRate); fill = 0; }
        }
        lastData = Environment.TickCount64;
    }

    void Analyze(int rate)
    {
        for (int i = 0; i < N; i++) { fft[i].X = (float)(buf[i] * FastFourierTransform.HannWindow(i, N)); fft[i].Y = 0; }
        FastFourierTransform.FFT(true, M, fft);
        double binHz = rate / (double)N;

        double bass = 0;                                // 40-160 Hz energy, linear
        for (int k = Math.Max(1, (int)(40 / binHz)); k <= Math.Max(1, (int)(160 / binHz)); k++)
            bass += fft[k].X * fft[k].X + fft[k].Y * fft[k].Y;
        long now = Environment.TickCount64;
        if (bass > bassAvg * BeatRatio && bass > 1e-7 && now - lastBeat > BeatGapMs) { lastBeat = now; Interlocked.Increment(ref beats); }
        bassAvg = bassAvg == 0 ? bass : bassAvg * 0.94 + bass * 0.06;   // ~0.4 s memory at ~47 analyses/s
        double vocal = 0;
        for (int k = (int)(300 / binHz); k <= (int)(3000 / binHz); k++)
            vocal += fft[k].X * fft[k].X + fft[k].Y * fft[k].Y;
        Bass = AutoGain(bass, ref bassPeakDb);
        Vocal = AutoGain(vocal, ref vocalPeakDb);
        for (int b = 0; b < B; b++)
        {
            double f0 = 40 * Math.Pow(400, b / (double)B), f1 = 40 * Math.Pow(400, (b + 1) / (double)B);  // 40 Hz .. 16 kHz
            int k0 = Math.Max(1, (int)(f0 / binHz)), k1 = Math.Min(N / 2, Math.Max(k0 + 1, (int)(f1 / binHz)));
            double m = 0;
            for (int k = k0; k < k1; k++) m = Math.Max(m, Math.Sqrt(fft[k].X * fft[k].X + fft[k].Y * fft[k].Y));
            double db = 20 * Math.Log10(m + 1e-9) + TiltDb * b / B;
            Bands[b] = (float)Math.Clamp((db - FloorDb) / RangeDb, 0, 1);
        }
    }
}
