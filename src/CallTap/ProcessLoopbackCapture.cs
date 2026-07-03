// ProcessLoopbackCapture.cs
// System-Audio-Aufnahme via WASAPI Process Loopback (AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK).
// Windows-Analogon zu den macOS Core Audio Process Taps, siehe Contract Abschnitt 1/6/7.
// Schreibt system.wav (Float32, fixes Format 48kHz/stereo, siehe Gotcha unten) und
// aktualisiert laufend den Pegel fuer levels.json ueber einen optionalen LevelWriter.

using System.Runtime.InteropServices;
using CallTap.Interop;
using CallTap.Recording;

namespace CallTap.Capture;

public sealed class AudioDataEventArgs : EventArgs
{
    public IntPtr Buffer { get; }
    public uint FrameCount { get; }
    public ushort Channels { get; }
    public uint SampleRate { get; }

    public AudioDataEventArgs(IntPtr buffer, uint frameCount, ushort channels, uint sampleRate)
    {
        Buffer = buffer;
        FrameCount = frameCount;
        Channels = channels;
        SampleRate = sampleRate;
    }
}

/// <summary>Gemeinsames Interface fuer Mic- und System-Capture (Contract Abschnitt 2, IAudioTrackCapture).</summary>
public interface IAudioTrackCapture
{
    event EventHandler<AudioDataEventArgs>? DataAvailable;
    void Stop();
}

/// <summary>
/// Aktiviert Process-Loopback-Capture fuer eine Ziel-PID (+ optional Kindprozessbaum)
/// und schreibt das Ergebnis nach system.wav. Kein Aggregate-Device-Trick wie bei
/// Core Audio noetig — der aktivierte IAudioClient IST bereits der Capture-Endpunkt.
/// </summary>
public sealed class ProcessLoopbackCapture : IAudioTrackCapture, IDisposable
{
    public event EventHandler<AudioDataEventArgs>? DataAvailable;

    // Fixes Aufnahmeformat: float32/48kHz/stereo. GetMixFormat() wird auf einem
    // process-loopback-aktivierten IAudioClient auf manchen Windows-Builds NICHT
    // unterstuetzt (E_NOTIMPL, siehe MS-Q&A-Thread in Contract 7.4) — deshalb hier
    // bewusst ein fest definiertes Format statt Geraete-Verhandlung, genau wie im
    // offiziellen ApplicationLoopback-Sample von Microsoft.
    private const ushort Channels = 2;
    private const uint SampleRate = 48000;
    private const ushort BitsPerSample = 32;
    private const ushort WaveFormatIeeeFloat = 3;

    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private Thread? _captureThread;
    private volatile bool _running;
    private LevelWriter? _levelWriter;

    /// <summary>
    /// Aktiviert Process-Loopback-Capture fuer <paramref name="targetPid"/> und startet
    /// einen Hintergrund-Capture-Thread, der nach <paramref name="outWavPath"/> schreibt.
    /// Mirrors SystemAudioRecorder.start(processes:outURL:) im Mac-Original.
    /// </summary>
    /// <param name="targetPid">Root-PID des Ziel-Prozesses (siehe Contract 4.2: Root-PID-Aufloesung).</param>
    /// <param name="excludeTree">true = nur der Zielprozess selbst, false (Default) = inkl. Kindprozessbaum.</param>
    /// <param name="outWavPath">Zielpfad fuer system.wav.</param>
    /// <param name="levelWriter">Optionaler gemeinsamer LevelWriter fuer levels.json (Contract 3.5).</param>
    public async Task StartAsync(int targetPid, bool excludeTree, string outWavPath, LevelWriter? levelWriter = null)
    {
        _levelWriter = levelWriter;

        var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
            {
                TargetProcessId = (uint)targetPid,
                ProcessLoopbackMode = excludeTree
                    ? PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
                    : PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
            },
        };

        int size = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr paramsPtr = Marshal.AllocCoTaskMem(size);
        IntPtr propvariantPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<PROPVARIANT_BLOB>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            var propvariant = new PROPVARIANT_BLOB
            {
                vt = PropVariantConstants.VT_BLOB,
                blob = new BLOB { cbSize = (uint)size, pBlobData = paramsPtr },
            };
            Marshal.StructureToPtr(propvariant, propvariantPtr, false);

            var handler = new ActivationCompletionHandler();
            NativeMethods.ActivateAudioInterfaceAsync(
                NativeMethods.VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                NativeMethods.IID_IAudioClient,
                propvariantPtr,
                handler,
                out _);

            _audioClient = await handler.Completion.ConfigureAwait(false);
        }
        finally
        {
            Marshal.FreeCoTaskMem(paramsPtr);
            Marshal.FreeCoTaskMem(propvariantPtr);
        }

        var format = new WAVEFORMATEX
        {
            wFormatTag = WaveFormatIeeeFloat,
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = BitsPerSample,
            nBlockAlign = (ushort)(Channels * (BitsPerSample / 8)),
            nAvgBytesPerSec = SampleRate * Channels * (uint)(BitsPerSample / 8),
            cbSize = 0,
        };

        _audioClient!.Initialize(
            AUDCLNT_SHAREMODE.Shared,
            // AUDCLNT_STREAMFLAGS_LOOPBACK ist auch bei Process-Loopback Pflicht
            // (wie im MS-ApplicationLoopback-Sample); ohne das Flag antwortet
            // WASAPI mit 0x88890021 AUDCLNT_E_INVALID_STREAM_FLAG.
            AUDCLNT_STREAMFLAGS.AUDCLNT_STREAMFLAGS_LOOPBACK,
            hnsBufferDuration: 200_0000, // 200ms in 100-ns-Einheiten
            hnsPeriodicity: 0,           // im Shared-Mode zwingend 0
            pFormat: ref format,
            audioSessionGuid: IntPtr.Zero);

        Guid iidCaptureClient = typeof(IAudioCaptureClient).GUID;
        _audioClient.GetService(iidCaptureClient, out var captureObj);
        _captureClient = (IAudioCaptureClient)captureObj;

        _running = true;
        _audioClient.Start();
        _captureThread = new Thread(() => CaptureLoop(outWavPath, format)) { IsBackground = true, Name = "CallTap-SystemCapture" };
        _captureThread.Start();
    }

    private void CaptureLoop(string outWavPath, WAVEFORMATEX format)
    {
        using var writer = new WavWriter(outWavPath, format.nChannels, format.nSamplesPerSec, format.wBitsPerSample, format.wFormatTag);
        const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

        while (_running)
        {
            _captureClient!.GetNextPacketSize(out uint framesAvailable);
            if (framesAvailable == 0)
            {
                Thread.Sleep(10);
                continue;
            }

            _captureClient.GetBuffer(out IntPtr buffer, out uint framesToRead,
                out uint flags, out _, out _);

            if (framesToRead > 0)
            {
                bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                if (!silent)
                {
                    writer.WriteFloat32Frames(buffer, framesToRead, format.nChannels);
                    _levelWriter?.UpdateSystem(LevelMeter.PeakFromFloat32(buffer, framesToRead, format.nChannels));
                }
                else
                {
                    _levelWriter?.UpdateSystem(0f);
                }
                DataAvailable?.Invoke(this, new AudioDataEventArgs(buffer, framesToRead, format.nChannels, format.nSamplesPerSec));
            }
            _captureClient.ReleaseBuffer(framesToRead);
        }
    }

    public void Stop()
    {
        _running = false;
        _captureThread?.Join(TimeSpan.FromSeconds(2));
        try { _audioClient?.Stop(); } catch (COMException) { /* bereits gestoppt/verworfen */ }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Kurzer, folgenloser Selbsttest fuer `calltap setup`/den Watch-Start-Selbsttest
    /// (Contract Abschnitt 5/6.2): aktiviert Process-Loopback gegen die eigene PID und
    /// gibt sofort wieder frei, ohne Nutzdaten dauerhaft zu behalten. Wirft bei
    /// Fehlschlag (z.B. Windows-Build &lt; 20348), damit der Aufrufer eine klare
    /// Fehlermeldung geben kann.
    /// </summary>
    public static async Task SelfTestAsync()
    {
        string tmpPath = Path.Combine(Path.GetTempPath(), $"calltap-selftest-{Environment.ProcessId}.wav");
        using var probe = new ProcessLoopbackCapture();
        try
        {
            await probe.StartAsync(Environment.ProcessId, excludeTree: false, outWavPath: tmpPath);
            await Task.Delay(200);
        }
        finally
        {
            probe.Stop();
            try { File.Delete(tmpPath); } catch (IOException) { }
        }
    }

    private sealed class ActivationCompletionHandler
        : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Completion => _tcs.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            activateOperation.GetActivateResult(out int hr, out object iface);
            if (hr != 0)
            {
                _tcs.TrySetException(Marshal.GetExceptionForHR(hr) ?? new COMException("ActivateAudioInterfaceAsync fehlgeschlagen", hr));
                return;
            }
            _tcs.TrySetResult((IAudioClient)iface);
        }
    }
}
