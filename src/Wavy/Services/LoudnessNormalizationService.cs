using System.Collections.Concurrent;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace Wavy.Services;

public sealed class LoudnessNormalizationService
{
    private readonly SettingsService _settings;
    public LoudnessNormalizationService(SettingsService settings) => _settings = settings;
    public bool IsEnabled => _settings.AudioNormalization || _settings.EqualizerBands.Any(x => Math.Abs(x) > 0.01);
    public const double TargetLufs = -16.0;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string> NormalizeAsync(string sourcePath, CancellationToken ct = default)
    {
        var bands = _settings.EqualizerBands;
        var normalize = _settings.AudioNormalization;
        if (!normalize && bands.All(x => Math.Abs(x) <= 0.01)) return sourcePath;
        var signature = $"v2:{normalize}:" + string.Join(",", bands.Select(x => x.ToString("F2", CultureInfo.InvariantCulture)));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..16];
        var outputPath = sourcePath + $".dsp-{hash}.wav";
        var gate = Locks.GetOrAdd(outputPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(outputPath) && File.GetLastWriteTimeUtc(outputPath) >= File.GetLastWriteTimeUtc(sourcePath))
                return outputPath;

            var tempPath = outputPath + ".partial.wav";
            TryDelete(tempPath);
            try
            {
                var loudness = await Task.Run(() => Measure(sourcePath, bands, ct), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var desiredGain = normalize ? Math.Clamp(TargetLufs - loudness.IntegratedLufs, -24.0, 18.0) : 0;
                var peakGain = loudness.Peak > 0 ? -2.0 - 20 * Math.Log10(loudness.Peak) : 0;
                var gainDb = Math.Min(desiredGain, peakGain);
                await Task.Run(() => Encode(sourcePath, tempPath, bands, gainDb, ct), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                File.Move(tempPath, outputPath, overwrite: true);
                foreach (var old in Directory.EnumerateFiles(Path.GetDirectoryName(sourcePath)!, Path.GetFileName(sourcePath) + ".dsp-*.wav"))
                    if (!string.Equals(old, outputPath, StringComparison.OrdinalIgnoreCase)) TryDelete(old);
                return outputPath;
            }
            finally
            {
                TryDelete(tempPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static LoudnessResult Measure(string path, double[] bands, CancellationToken ct)
    {
        using var reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
        var samples = new EqualizerSampleProvider(reader.ToSampleProvider(), bands);
        var channels = samples.WaveFormat.Channels;
        var rate = samples.WaveFormat.SampleRate;
        var blockSamples = Math.Max(1, (int)(rate * 0.4));
        var hopSamples = Math.Max(1, (int)(rate * 0.1));
        var frame = new float[Math.Max(1, channels) * hopSamples];
        var filters = Enumerable.Range(0, channels).Select(_ => new KWeightingFilter(rate)).ToArray();
        var blockEnergy = new List<double>();
        var ring = new double[blockSamples];
        var ringIndex = 0;
        var windowFrames = 0;
        var hopFrames = 0;
        var rollingEnergy = 0.0;
        var peak = 0.0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = samples.Read(frame, 0, frame.Length);
            if (read == 0) break;
            for (var i = 0; i + channels <= read; i += channels)
            {
                var frameEnergy = 0.0;
                for (var ch = 0; ch < channels; ch++)
                {
                    peak = Math.Max(peak, Math.Abs(frame[i + ch]));
                    var value = filters[ch].Process(frame[i + ch]);
                    frameEnergy += value * value;
                }
                if (windowFrames == blockSamples)
                {
                    rollingEnergy -= ring[ringIndex];
                    windowFrames--;
                }
                ring[ringIndex] = frameEnergy;
                rollingEnergy += frameEnergy;
                ringIndex = (ringIndex + 1) % blockSamples;
                windowFrames++;
                hopFrames++;

                if (windowFrames == blockSamples && hopFrames >= hopSamples)
                {
                    if (rollingEnergy > 0) blockEnergy.Add(rollingEnergy / blockSamples);
                    hopFrames = 0;
                }
            }
        }

        if (blockEnergy.Count == 0)
            throw new InvalidDataException("The track has no measurable audio samples.");

        var absoluteGate = Math.Pow(10, (-70.0 + 0.691) / 10.0);
        var absolute = blockEnergy.Where(e => e >= absoluteGate).ToArray();
        if (absolute.Length == 0) absolute = blockEnergy.ToArray();
        var ungatedLufs = -0.691 + 10 * Math.Log10(absolute.Average());
        var relativeGate = Math.Pow(10, (ungatedLufs - 10 + 0.691) / 10.0);
        var gated = absolute.Where(e => e >= relativeGate).ToArray();
        if (gated.Length == 0) gated = absolute;
        var energySum = gated.Average();
        return new LoudnessResult(-0.691 + 10 * Math.Log10(energySum), peak);
    }

    private static void Encode(string sourcePath, string outputPath, double[] bands, double gainDb, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var reader = new MediaFoundationReader(sourcePath, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
        var samples = new EqualizerSampleProvider(reader.ToSampleProvider(), bands);
        var gain = (float)Math.Pow(10, gainDb / 20.0);
        using var writer = new WaveFileWriter(outputPath, samples.WaveFormat);
        var buffer = new float[samples.WaveFormat.SampleRate * samples.WaveFormat.Channels];
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++) buffer[i] *= gain;
            writer.WriteSamples(buffer, 0, read);
        }
        ct.ThrowIfCancellationRequested();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private readonly record struct LoudnessResult(double IntegratedLufs, double Peak);

    private sealed class KWeightingFilter
    {
        private readonly Biquad _shelf;
        private readonly Biquad _highPass;

        public KWeightingFilter(int sampleRate)
        {
            _shelf = Biquad.HighShelf(sampleRate, 1681.974, 4.0, 0.707);
            _highPass = Biquad.HighPass(sampleRate, 38.135, 0.5003);
        }

        public double Process(double value) => _highPass.Process(_shelf.Process(value));
    }

    private sealed class Biquad
    {
        private readonly double b0, b1, b2, a1, a2;
        private double x1, x2, y1, y2;
        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            this.b0 = b0 / a0; this.b1 = b1 / a0; this.b2 = b2 / a0;
            this.a1 = a1 / a0; this.a2 = a2 / a0;
        }
        public double Process(double x)
        {
            var y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            return y;
        }
        public static Biquad HighPass(int sr, double frequency, double q)
        {
            var w = 2 * Math.PI * frequency / sr; var c = Math.Cos(w); var a = Math.Sin(w) / (2 * q);
            return new Biquad((1+c)/2, -(1+c), (1+c)/2, 1+a, -2*c, 1-a);
        }
        public static Biquad HighShelf(int sr, double frequency, double gainDb, double slope)
        {
            var a = Math.Pow(10, gainDb / 40); var w = 2 * Math.PI * frequency / sr;
            var c = Math.Cos(w); var s = Math.Sin(w);
            var alpha = s / 2 * Math.Sqrt((a + 1 / a) * (1 / slope - 1) + 2);
            var beta = 2 * Math.Sqrt(a) * alpha;
            var b0 = a * ((a + 1) + (a - 1) * c + beta);
            var b1 = -2 * a * ((a - 1) + (a + 1) * c);
            var b2 = a * ((a + 1) + (a - 1) * c - beta);
            var a0 = (a + 1) - (a - 1) * c + beta;
            var a1 = 2 * ((a - 1) - (a + 1) * c);
            var a2 = (a + 1) - (a - 1) * c - beta;
            return new Biquad(b0, b1, b2, a0, a1, a2);
        }
    }

    private sealed class EqualizerSampleProvider : ISampleProvider
    {
        private static readonly float[] Frequencies = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        private readonly ISampleProvider _source;
        private readonly BiQuadFilter[][] _filters;
        private int _channel;
        public EqualizerSampleProvider(ISampleProvider source, double[] bands)
        {
            _source = source;
            _filters = Enumerable.Range(0, source.WaveFormat.Channels).Select(_ =>
                Frequencies.Select((frequency, i) => (frequency, i))
                    .Where(x => x.frequency < source.WaveFormat.SampleRate / 2f && Math.Abs(bands[x.i]) > 0.01)
                    .Select(x => BiQuadFilter.PeakingEQ(source.WaveFormat.SampleRate, x.frequency, 1.4f, (float)bands[x.i])).ToArray()).ToArray();
        }
        public WaveFormat WaveFormat => _source.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);
            for (var i = offset; i < offset + read; i++)
            {
                foreach (var filter in _filters[_channel]) buffer[i] = filter.Transform(buffer[i]);
                _channel = (_channel + 1) % WaveFormat.Channels;
            }
            return read;
        }
    }
}
