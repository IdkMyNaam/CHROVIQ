using System.Runtime.InteropServices;

namespace WindowsCustomizer;

/// <summary>
/// Master volume of the default speakers, through Windows' Core Audio API (COM; no extra libraries).
/// Every call looks the default device up again, so switching speakers/headphones just works.
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

    // ---------- Core Audio COM declarations (methods must stay in this exact order) ----------

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
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
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
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
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
