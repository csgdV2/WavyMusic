using System.Collections.Concurrent;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace Wavy.Services;

public sealed class LoudnessNormalizationService
{
    private readonly SettingsService _settings;
    private readonly CacheService? _cache;
    public LoudnessNormalizationService(SettingsService settings, CacheService? cache = null)
    {
        _settings = settings;
        _cache = cache;
    }
    public bool IsEnabled => _settings.AudioNormalization || _settings.EqualizerBands.Any(x => Math.Abs(x) > 0.01);
    public const double TargetLufs = -16.0;

    private static readonly ConcurrentDictionary<string, LoudnessResult> Measurements = new(StringComparer.OrdinalIgnoreCase);

    public async Task<MediaSource> CreateSourceAsync(string sourcePath, CancellationToken ct = default)
    {
        var audio = await OpenAudioAsync(sourcePath, ct).ConfigureAwait(false);
        try { return CreatePlaybackSource(audio); }
        catch { audio.Dispose(); throw; }
    }

    internal async Task<PlaybackAudio> OpenAudioAsync(string sourcePath, CancellationToken ct = default)
    {
        var bands = _settings.EqualizerBands.ToArray();
        var normalize = _settings.AudioNormalization;
        var file = new FileInfo(sourcePath);
        var signature = "v1:" + sourcePath + ":" + file.LastWriteTimeUtc.Ticks + ":" + file.Length + ":" +
            string.Join(",", bands.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
        if (!Measurements.TryGetValue(signature, out var loudness))
        {
            if (_cache?.GetAudioAnalysis(sourcePath, signature) is { } saved)
                loudness = new LoudnessResult(saved.IntegratedLufs, saved.Peak);
            else
            {
                loudness = await Task.Run(() => Measure(sourcePath, bands, ct), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                _cache?.SaveAudioAnalysis(sourcePath, signature, loudness.IntegratedLufs, loudness.Peak);
            }
            Measurements[signature] = loudness;
        }
        var desiredGain = normalize ? Math.Clamp(TargetLufs - loudness.IntegratedLufs, -24.0, 18.0) : 0;
        var peakGain = loudness.Peak > 0 ? -2.0 - 20 * Math.Log10(loudness.Peak) : 0;
        var gain = (float)Math.Pow(10, Math.Min(desiredGain, peakGain) / 20.0);
        ct.ThrowIfCancellationRequested();
        return new PlaybackAudio(sourcePath, bands, gain);
    }

    internal sealed class PlaybackAudio : ISampleProvider, IDisposable
    {
        private readonly MediaFoundationReader _reader;
        private readonly double[] _bands;
        private readonly float _gain;
        private EqualizerSampleProvider _samples;
        private long _framePosition;
        public WaveFormat WaveFormat => _samples.WaveFormat;
        public TimeSpan Duration => _reader.TotalTime;
        public TimeSpan Position => TimeSpan.FromSeconds((double)_framePosition / WaveFormat.SampleRate);

        public PlaybackAudio(string path, double[] bands, float gain)
        {
            _reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
            _bands = bands;
            _gain = gain;
            _samples = new EqualizerSampleProvider(_reader.ToSampleProvider(), bands);
        }

        public void Seek(TimeSpan position)
        {
            _reader.CurrentTime = position;
            _samples = new EqualizerSampleProvider(_reader.ToSampleProvider(), _bands);
            _framePosition = _reader.Position / _reader.WaveFormat.BlockAlign;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _samples.Read(buffer, offset, count);
            for (var i = offset; i < offset + read; i++) buffer[i] *= _gain;
            _framePosition += read / WaveFormat.Channels;
            return read;
        }

        public void Dispose() => _reader.Dispose();
    }

    private static MediaSource CreatePlaybackSource(PlaybackAudio audio)
    {
        var rate = audio.WaveFormat.SampleRate;
        var channels = audio.WaveFormat.Channels;
        var properties = AudioEncodingProperties.CreatePcm((uint)rate, (uint)channels, 32);
        properties.Subtype = MediaEncodingSubtypes.Float;
        var stream = new MediaStreamSource(new AudioStreamDescriptor(properties))
        {
            CanSeek = true,
            Duration = audio.Duration,
            BufferTime = TimeSpan.FromMilliseconds(250),
        };
        var gate = new object();
        var closed = false;
        var buffer = new float[Math.Max(1, rate / 50) * channels];
        stream.Starting += (_, args) =>
        {
            try
            {
                lock (gate)
                {
                    if (closed) return;
                    if (args.Request.StartPosition is { } position) audio.Seek(position);
                    args.Request.SetActualStartPosition(audio.Position);
                }
            }
            catch { stream.NotifyError(MediaStreamSourceErrorStatus.Other); }
        };
        stream.SampleRequested += (_, args) =>
        {
            try
            {
                lock (gate)
                {
                    if (closed) return;
                    var start = audio.Position;
                    var read = audio.Read(buffer, 0, buffer.Length);
                    if (read == 0) return;
                    var bytes = new byte[read * sizeof(float)];
                    Buffer.BlockCopy(buffer, 0, bytes, 0, bytes.Length);
                    var sample = MediaStreamSample.CreateFromBuffer(bytes.AsBuffer(), start);
                    sample.Duration = audio.Position - start;
                    args.Request.Sample = sample;
                }
            }
            catch { stream.NotifyError(MediaStreamSourceErrorStatus.Other); }
        };
        stream.Closed += (_, _) =>
        {
            lock (gate)
            {
                if (closed) return;
                closed = true;
                audio.Dispose();
            }
        };
        return MediaSource.CreateFromMediaStreamSource(stream);
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
