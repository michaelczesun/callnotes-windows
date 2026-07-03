// Interop.cs
// Rohes COM/P-Invoke fuer WASAPI Process-Loopback-Aktivierung, siehe Contract Abschnitt 7.
// Bewusst NICHT ueber NAudio geloest — NAudio deckt keinen Process-Loopback ab
// (NAudio Issue #878). Die GUIDs fuer die Completion-Handler-Interfaces sind
// dieselben, die in NAudios eigener WasapiOutRT.cs bereits produktiv verwendet
// werden (siehe Contract Abschnitt 1/7.2), NICHT aus den GUID-losen MS-Learn-Seiten
// neu abgeleitet.

using System.Runtime.InteropServices;

namespace CallTap.Interop;

/// <summary>
/// AUDIOCLIENT_ACTIVATION_TYPE — audioclientactivationparams.h.
/// https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ne-audioclientactivationparams-audioclient_activation_type
/// </summary>
internal enum AUDIOCLIENT_ACTIVATION_TYPE
{
    AUDIOCLIENT_ACTIVATION_TYPE_DEFAULT = 0,
    AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1,
}

/// <summary>
/// PROCESS_LOOPBACK_MODE — audioclientactivationparams.h. Min. unterstuetzter Client:
/// Windows 10 Build 20348.
/// https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ne-audioclientactivationparams-process_loopback_mode
/// </summary>
internal enum PROCESS_LOOPBACK_MODE
{
    PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0,
    PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1,
}

/// <summary>
/// AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS — audioclientactivationparams.h.
/// https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_process_loopback_params
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
{
    public uint TargetProcessId;
    public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
}

/// <summary>
/// AUDIOCLIENT_ACTIVATION_PARAMS — in C++ ein Tagged Union (ActivationType +
/// anonymer Union, aktuell nur ProcessLoopbackParams). C# kennt keine echte Union;
/// [FieldOffset] auf beiden Feldern bildet dasselbe Speicherlayout nach.
/// https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_activation_params
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct AUDIOCLIENT_ACTIVATION_PARAMS
{
    [FieldOffset(0)] public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
    // Offset 4: setzt voraus, dass das Enum als 4-Byte-DWORD marshalt wird,
    // passend zum int-basierten Enum in audioclientactivationparams.h.
    [FieldOffset(4)] public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
}

/// <summary>
/// Minimale PROPVARIANT-Form, ausreichend fuer den VT_BLOB-Fall, den
/// ActivateAudioInterfaceAsync fuer das activationParams-Argument hier braucht.
/// Das vollstaendige PROPVARIANT ist ein viel groesserer Tagged Union; hier
/// werden nur vt/blob benutzt.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BLOB
{
    public uint cbSize;
    public IntPtr pBlobData;
}

[StructLayout(LayoutKind.Explicit)]
internal struct PROPVARIANT_BLOB
{
    [FieldOffset(0)] public ushort vt;     // VT_BLOB = 65
    [FieldOffset(8)] public BLOB blob;     // Offset 8: passt zum 8-Byte-Header von
                                            // PROPVARIANT (vt + wReserved1-3) vor dem
                                            // Union-Payload auf x64.
}

internal static class PropVariantConstants
{
    internal const ushort VT_BLOB = 65;
}

[ComImport]
[Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    void GetActivateResult(
        out int activateResult,
        [MarshalAs(UnmanagedType.IUnknown)] out object activateInterface);
}

[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

/// <summary>
/// Minimale IAudioClient-Untermenge, die ProcessLoopbackCapture tatsaechlich
/// benutzt. IID aus audioclient.h (wohlbekannt, seit Vista WASAPI unveraendert).
/// </summary>
[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    void Initialize(
        AUDCLNT_SHAREMODE shareMode,
        AUDCLNT_STREAMFLAGS streamFlags,
        long hnsBufferDuration,
        long hnsPeriodicity,
        [In] ref WAVEFORMATEX pFormat,
        [In] IntPtr audioSessionGuid);

    void GetBufferSize(out uint numBufferFrames);
    void GetStreamLatency(out long latency);
    void GetCurrentPadding(out uint numPaddingFrames);
    void IsFormatSupported(AUDCLNT_SHAREMODE shareMode, [In] ref WAVEFORMATEX format, out IntPtr closestMatch);
    void GetMixFormat(out IntPtr deviceFormatPtr);
    void GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
    void Start();
    void Stop();
    void Reset();
    void SetEventHandle(IntPtr eventHandle);
    void GetService([MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

internal enum AUDCLNT_SHAREMODE { Shared = 0, Exclusive = 1 }

[Flags]
internal enum AUDCLNT_STREAMFLAGS : uint
{
    None = 0,
    AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000,
    // Nur fuer den klassischen Full-Endpoint-Fallback relevant, NICHT fuer
    // Process-Loopback (dort impliziert bereits der Aktivierungstyp selbst
    // Loopback-Semantik).
    AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000,
}

[StructLayout(LayoutKind.Sequential)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

/// <summary>IAudioCaptureClient — audioclient.h, wohlbekannte IID.</summary>
[ComImport]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    void GetBuffer(out IntPtr dataBuffer, out uint numFramesToRead,
        out uint bufferFlags, out ulong devicePosition, out ulong qpcPosition);
    void ReleaseBuffer(uint numFramesRead);
    void GetNextPacketSize(out uint numFramesInNextPacket);
}

internal static class NativeMethods
{
    internal const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = @"VAD\Process_Loopback";

    // IID_IAudioClient (audioclient.h) — wird als `riid` uebergeben, um bei der
    // Aktivierung ein IAudioClient zurueckzubekommen.
    internal static readonly Guid IID_IAudioClient =
        new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    internal static extern void ActivateAudioInterfaceAsync(
        [In, MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [In] IntPtr activationParams, // IntPtr auf ein gepinntes PROPVARIANT_BLOB, oder IntPtr.Zero fuer Default-Aktivierung
        [In] IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("ole32.dll")]
    internal static extern IntPtr CoTaskMemAlloc(nuint cb);

    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(IntPtr pv);

    // PreserveSig = false (wie bei WasapiOutRT) heisst: die CLR wirft bei
    // nicht-S_OK-HRESULT automatisch eine COM-Exception, statt es zurueckzugeben —
    // deshalb hat die Signatur oben keinen HRESULT/int-Rueckgabewert, obwohl die
    // native Funktion einen liefert.
}
