// WavWriter.cs
// Streaming-WAV-Writer fuer die beiden Aufnahmespuren (mic.wav / system.wav) und
// den levels.json-Writer (alle 0.35s ins rec-Verzeichnis, Format wie Mac-Original:
// {"mic":0..1,"sys":0..1,"t":unix}) — siehe Contract Abschnitt 3.5/3.7.

using System.Globalization;

namespace CallTap.Recording;

/// <summary>
/// Minimaler, streaming-faehiger WAV-Writer. Unterstuetzt sowohl 32-bit-IEEE-Float
/// (System-Loopback-Pfad, Rohbuffer liegt als nativer Zeiger vor) als auch PCM16
/// (Mikrofon-Pfad, NAudio liefert ein verwaltetes byte[]). Schreibt sofort einen
/// RIFF/fmt/data-Header mit Platzhalter-Groessen, die beim Dispose() anhand der
/// tatsaechlich geschriebenen Byte-Anzahl gepatcht werden (Groesse steht bei einer
/// Streaming-Aufnahme erst am Ende fest).
/// </summary>
public sealed class WavWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private readonly ushort _channels;
    private readonly uint _sampleRate;
    private readonly ushort _bitsPerSample;
    private readonly ushort _formatTag;
    private long _dataBytesWritten;
    private bool _disposed;

    /// <param name="formatTag">1 = WAVE_FORMAT_PCM, 3 = WAVE_FORMAT_IEEE_FLOAT.</param>
    public WavWriter(string path, ushort channels, uint sampleRate, ushort bitsPerSample, ushort formatTag)
    {
        _channels = channels;
        _sampleRate = sampleRate;
        _bitsPerSample = bitsPerSample;
        _formatTag = formatTag;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new BinaryWriter(_stream);
        WriteHeaderPlaceholder();
    }

    private void WriteHeaderPlaceholder()
    {
        ushort blockAlign = (ushort)(_channels * (_bitsPerSample / 8));
        uint byteRate = _sampleRate * blockAlign;

        _writer.Write(new[] { 'R', 'I', 'F', 'F' });
        _writer.Write(0u); // ChunkSize-Platzhalter, wird in Dispose() gepatcht
        _writer.Write(new[] { 'W', 'A', 'V', 'E' });

        _writer.Write(new[] { 'f', 'm', 't', ' ' });
        _writer.Write(16u); // Subchunk1Size fuer PCM/IEEE-Float ohne Extension
        _writer.Write(_formatTag);
        _writer.Write(_channels);
        _writer.Write(_sampleRate);
        _writer.Write(byteRate);
        _writer.Write(blockAlign);
        _writer.Write(_bitsPerSample);

        _writer.Write(new[] { 'd', 'a', 't', 'a' });
        _writer.Write(0u); // Subchunk2Size-Platzhalter, wird in Dispose() gepatcht
    }

    /// <summary>Schreibt rohe Float32-Interleaved-Frames direkt aus einem nativen Buffer (Zero-Copy).</summary>
    public unsafe void WriteFloat32Frames(IntPtr nativeBuffer, uint frameCount, ushort channels)
    {
        if (_disposed) return;
        int byteCount = (int)(frameCount * channels * sizeof(float));
        if (byteCount <= 0) return;

        var span = new ReadOnlySpan<byte>((void*)nativeBuffer, byteCount);
        _stream.Write(span);
        _dataBytesWritten += byteCount;
    }

    /// <summary>Schreibt rohe Bytes (PCM16 oder sonstiges) aus einem verwalteten Puffer.</summary>
    public void WriteBytes(byte[] buffer, int offset, int count)
    {
        if (_disposed) return;
        if (count <= 0) return;
        _stream.Write(buffer, offset, count);
        _dataBytesWritten += count;
    }

    /// <summary>Patcht RIFF-/data-Groessenfelder mit der tatsaechlich geschriebenen Laenge.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _writer.Flush();

        long fileLength = _stream.Length;
        uint riffChunkSize = (uint)Math.Max(0, fileLength - 8);
        uint dataChunkSize = (uint)_dataBytesWritten;

        _stream.Seek(4, SeekOrigin.Begin);
        _writer.Write(riffChunkSize);

        _stream.Seek(40, SeekOrigin.Begin);
        _writer.Write(dataChunkSize);

        _writer.Flush();
        _writer.Dispose();
        _stream.Dispose();
    }
}

/// <summary>
/// Schreibt periodisch (alle 0.35s, wie im Mac-Original) &lt;rec-dir&gt;/levels.json mit
/// dem zuletzt gemessenen linearen Peak-Pegel (0..1) von Mic- und System-Spur.
/// Contract Abschnitt 3.5: {"mic":0..1,"sys":0..1,"t":unix}.
/// </summary>
public sealed class LevelWriter : IDisposable
{
    private readonly string _levelsPath;
    private readonly Timer _timer;
    private readonly object _lock = new();
    private float _micLevel;
    private float _sysLevel;
    private bool _disposed;

    public LevelWriter(string recDir)
    {
        Directory.CreateDirectory(recDir);
        _levelsPath = Path.Combine(recDir, "levels.json");
        _timer = new Timer(_ => WriteNow(), null, TimeSpan.FromMilliseconds(350), TimeSpan.FromMilliseconds(350));
    }

    public void UpdateMic(float level) { lock (_lock) { _micLevel = Clamp01(level); } }
    public void UpdateSystem(float level) { lock (_lock) { _sysLevel = Clamp01(level); } }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    private void WriteNow()
    {
        if (_disposed) return;
        float mic, sys;
        lock (_lock) { mic = _micLevel; sys = _sysLevel; }

        double unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        string json = string.Format(
            CultureInfo.InvariantCulture,
            "{{\"mic\":{0:0.###},\"sys\":{1:0.###},\"t\":{2:0.000}}}",
            mic, sys, unixSeconds);

        try
        {
            string tmpPath = _levelsPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _levelsPath, overwrite: true);
        }
        catch (IOException)
        {
            // levels.json ist rein informativ fuer eine Tray-UI — ein verpasster
            // Tick ist unkritisch, die Aufnahme darf dafuer nicht abbrechen.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        try { File.Delete(_levelsPath); } catch (IOException) { /* best effort */ }
    }
}

/// <summary>Peak-Pegel-Berechnung (0..1) fuer Float32- und PCM16-Buffer.</summary>
public static class LevelMeter
{
    public static unsafe float PeakFromFloat32(IntPtr nativeBuffer, uint frameCount, ushort channels)
    {
        if (frameCount == 0 || channels == 0) return 0f;
        float* samples = (float*)nativeBuffer;
        int total = (int)(frameCount * channels);
        float peak = 0f;
        for (int i = 0; i < total; i++)
        {
            float abs = MathF.Abs(samples[i]);
            if (abs > peak) peak = abs;
        }
        return peak > 1f ? 1f : peak;
    }

    /// <summary>Peak aus einem verwalteten Float32-Byte-Array (Little-Endian), wie es NAudio liefert.</summary>
    public static float PeakFromFloat32Bytes(byte[] buffer, int byteCount)
    {
        if (byteCount <= 0) return 0f;
        float peak = 0f;
        int sampleCount = byteCount / 4;
        for (int i = 0; i < sampleCount; i++)
        {
            float sample = BitConverter.ToSingle(buffer, i * 4);
            float abs = MathF.Abs(sample);
            if (abs > peak) peak = abs;
        }
        return peak > 1f ? 1f : peak;
    }

    public static float PeakFromPcm16(byte[] buffer, int offset, int byteCount)
    {
        if (byteCount <= 0) return 0f;
        float peak = 0f;
        int sampleCount = byteCount / 2;
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(buffer[offset + i * 2] | (buffer[offset + i * 2 + 1] << 8));
            float norm = MathF.Abs(sample / 32768f);
            if (norm > peak) peak = norm;
        }
        return peak > 1f ? 1f : peak;
    }
}
