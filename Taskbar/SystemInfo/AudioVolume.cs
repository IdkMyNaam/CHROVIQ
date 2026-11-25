using System.Runtime.InteropServices;

namespace WindowsCustomizer;

/// <summary>
/// Master volume of the default speakers, through Windows' Core Audio API (COM; no extra libraries).
/// Every call looks the default device up again, so switching speakers/headphones just works.
/// <see cref="Changed"/> reports changes, so nobody has to ask every second.
/// </summary>
internal static class AudioVolume
{
    /// <summary>Volume 0–100, and whether it is muted. Null when there is no audio device.</summary>
    public static (int percent, bool muted)? Get()
    {
        try
        {
            var endpoint = DefaultEndpoint();
            if (endpoint == null) return null;
            try
            {
                Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out float level));
                Marshal.ThrowExceptionForHR(endpoint.GetMute(out bool muted));
                return ((int)Math.Round(level * 100), muted);
            }
            finally
            {
                Marshal.ReleaseComObject(endpoint);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Sets the volume (0–100). Changing the volume also unmutes, like the Windows volume slider.</summary>
    public static void Set(int percent)
    {
        Use(endpoint =>
        {
            var context = Guid.Empty;
            endpoint.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref context);
            endpoint.SetMute(false, ref context);
        });
    }

    public static void ToggleMute()
    {
        Use(endpoint =>
        {
            var context = Guid.Empty;
            endpoint.GetMute(out bool muted);
            endpoint.SetMute(!muted, ref context);
        });
    }

    private static void Use(Action<IAudioEndpointVolume> action)
    {
        try
        {
            var endpoint = DefaultEndpoint();
            if (endpoint == null) return;
            try { action(endpoint); }
            finally { Marshal.ReleaseComObject(endpoint); }
        }
        catch (Exception)
        {
            // No device, or it went away while we used it: nothing to change.
        }
    }

    private static IAudioEndpointVolume? DefaultEndpoint()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device) != 0 || device == null)
                return null;
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var endpoint));
                return (IAudioEndpointVolume)endpoint;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    // ---------- Change notifications ----------

    private static readonly object WatchLock = new();
    private static IMMDeviceEnumerator? _watchEnumerator;
    private static IAudioEndpointVolume? _watchEndpoint;
    private static DeviceListener? _deviceListener;
    private static VolumeListener? _volumeListener;
    private static Action? _changed;

    /// <summary>
    /// Raised when the volume or mute changes (from anywhere: keys, the Windows slider, other apps), or the
    /// default speakers change. Raised on a Windows audio thread: hand it to the UI thread yourself.
    /// Listening starts with the first subscriber and stops with the last.
    /// </summary>
    public static event Action Changed
    {
        add
        {
            lock (WatchLock)
            {
                _changed += value;
                if (_watchEnumerator == null) StartWatching();
            }
        }
        remove
        {
            lock (WatchLock)
            {
                _changed -= value;
                if (_changed == null) StopWatching();
            }
        }
    }

    private static void RaiseChanged() => _changed?.Invoke();

    private static void StartWatching()
    {
        try
        {
            _watchEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            _deviceListener = new DeviceListener();
            _watchEnumerator.RegisterEndpointNotificationCallback(_deviceListener);
            WatchDefaultEndpoint();
        }
        catch (Exception) { StopWatching(); } // no audio service: the module just shows what Get() returns
    }

    /// <summary>Listens to the current default speakers (again, after they changed).</summary>
    private static void WatchDefaultEndpoint()
    {
        if (_watchEndpoint != null && _volumeListener != null)
        {
            try { _watchEndpoint.UnregisterControlChangeNotify(_volumeListener); } catch (Exception) { }
            Marshal.ReleaseComObject(_watchEndpoint);
            _watchEndpoint = null;
        }
        _watchEndpoint = DefaultEndpoint();
        if (_watchEndpoint == null) return;
        _volumeListener ??= new VolumeListener();
        _watchEndpoint.RegisterControlChangeNotify(_volumeListener);
    }

    private static void StopWatching()
    {
        try
        {
            if (_watchEndpoint != null)
            {
                if (_volumeListener != null) _watchEndpoint.UnregisterControlChangeNotify(_volumeListener);
                Marshal.ReleaseComObject(_watchEndpoint);
            }
            if (_watchEnumerator != null)
            {
                if (_deviceListener != null) _watchEnumerator.UnregisterEndpointNotificationCallback(_deviceListener);
                Marshal.ReleaseComObject(_watchEnumerator);
            }
        }
        catch (Exception) { /* the audio service went away */ }
        _watchEndpoint = null;
        _watchEnumerator = null;
    }

    /// <summary>Windows calls this when the volume or mute of the watched speakers changes.</summary>
    private sealed class VolumeListener : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr data) { RaiseChanged(); return 0; }
    }

    /// <summary>Windows calls this when audio devices change; only a new default output matters here.</summary>
    private sealed class DeviceListener : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, int newState) => 0;
        public int OnDeviceAdded(string deviceId) => 0;
        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            if (flow != EDataFlow.Render || role != ERole.Multimedia) return 0;
            // Not on this callback's thread (Windows forbids waiting on audio calls here): on a pool thread.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (WatchLock)
                {
                    if (_watchEnumerator == null) return;
                    try { WatchDefaultEndpoint(); } catch (Exception) { }
                }
                RaiseChanged();
            });
            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }

    // ---------- Core Audio COM declarations (methods must stay in this exact order) ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public int PropertyId; }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr notifyData);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    private const int CLSCTX_ALL = 0x17;

    private enum EDataFlow { Render = 0 }

    private enum ERole { Multimedia = 1 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
        int OpenPropertyStore(int access, out IntPtr properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }
}
