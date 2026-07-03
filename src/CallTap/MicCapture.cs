// MicCapture.cs
// Mikrofon-Aufnahme via NAudio WasapiCapture (Standard-Aufnahme-Endpunkt), siehe
// Contract Abschnitt 1/7.5: NAudio deckt den normalen Mikrofon-Pfad vollstaendig ab,
// nur der Process-Loopback-Pfad braucht rohes COM-Interop (ProcessLoopbackCapture.cs).
// Schreibt mic.wav (PCM16 oder Float32 je nach Mix-Format des Geraets, unveraendert
// wie vom Treiber geliefert, kein Resampling beim Aufnehmen).

using NAudio.CoreAudioApi;
using NAudio.Wave;
using CallTap.Recording;

namespace CallTap.Capture;

public sealed class MicCapture : IAudioTrackCapture, IDisposable
{
    public event EventHandler<AudioDataEventArgs>? DataAvailable;

    private WasapiCapture? _capture;
    private WavWriter? _writer;
    private LevelWriter? _levelWriter;
    private bool _isFloat;

    /// <summary>
    /// Startet die Mikrofon-Aufnahme auf dem Standard-Capture-Geraet (oder
    /// <paramref name="deviceId"/>, falls in config.json micDeviceId gesetzt ist,
    /// siehe Contract Abschnitt 3.1) und schreibt fortlaufend nach
    /// <paramref name="outWavPath"/>.
    /// </summary>
    public void Start(string outWavPath, string? deviceId = null, LevelWriter? levelWriter = null)
    {
        _levelWriter = levelWriter;

        using var enumerator = new MMDeviceEnumerator();
        MMDevice device = string.IsNullOrEmpty(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : enumerator.GetDevice(deviceId);

        _capture = new WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared };

        var waveFormat = _capture.WaveFormat;
        _isFloat = waveFormat.Encoding == WaveFormatEncoding.IeeeFloat;
        ushort formatTag = (ushort)(_isFloat ? 3 /* WAVE_FORMAT_IEEE_FLOAT */ : 1 /* WAVE_FORMAT_PCM */);

        _writer = new WavWriter(
            outWavPath,
            (ushort)waveFormat.Channels,
            (uint)waveFormat.SampleRate,
            (ushort)waveFormat.BitsPerSample,
            formatTag);

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_writer == null || e.BytesRecorded <= 0 || _capture == null) return;

        _writer.WriteBytes(e.Buffer, 0, e.BytesRecorded);

        float peak = _isFloat
            ? LevelMeter.PeakFromFloat32Bytes(e.Buffer, e.BytesRecorded)
            : LevelMeter.PeakFromPcm16(e.Buffer, 0, e.BytesRecorded);
        _levelWriter?.UpdateMic(peak);

        int blockAlign = Math.Max(1, _capture.WaveFormat.BlockAlign);
        DataAvailable?.Invoke(this,
            new AudioDataEventArgs(IntPtr.Zero, (uint)(e.BytesRecorded / blockAlign),
                (ushort)_capture.WaveFormat.Channels, (uint)_capture.WaveFormat.SampleRate));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _writer?.Dispose();
        _writer = null;
    }

    public void Stop() => _capture?.StopRecording();

    public void Dispose()
    {
        _capture?.Dispose();
        _writer?.Dispose();
    }
}
