using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace ETSAssistant.WinUI3.Services;

public sealed record DefaultAudioInputStatus(string DeviceId, string Name, bool IsVirtualCable);

/// <summary>
/// Routes the ETS processes to the virtual capture endpoint using Windows' per-application
/// audio policy. It deliberately never changes the machine-wide default input.
/// </summary>
public sealed class DefaultAudioInputService
{
    private static readonly Lazy<DefaultAudioInputService> Lazy = new(() => new DefaultAudioInputService());
    public static DefaultAudioInputService Current => Lazy.Value;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(int ProcessId, EndpointRole Role), string?> _previousRoutes = new();
    private readonly Dictionary<(int ProcessId, EndpointRole Role), string> _routedEndpoints = new();
    private VirtualCableDevice? _captureDevice;
    private DateTimeOffset _captureDeviceCheckedAt;

    public bool LastSetVirtualDefaultSucceeded { get; private set; }
    public bool HasActiveEtsRoute => _routedEndpoints.Count > 0;
    public string? RoutedDeviceName => _captureDevice?.Name;

    /// <summary>Reports the Windows-wide default input; this service does not modify it.</summary>
    public async Task<DefaultAudioInputStatus?> GetStatusAsync()
    {
        string id = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
        if (string.IsNullOrWhiteSpace(id)) return null;
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
        DeviceInformation? match = devices.FirstOrDefault(device => string.Equals(device.Id, id,
            StringComparison.OrdinalIgnoreCase));
        string name = match?.Name ?? id;
        return new DefaultAudioInputStatus(id, name, IsVirtualName(name));
    }

    /// <summary>Applies the virtual microphone only to the currently running ETS processes.</summary>
    public async Task<string> BeginSessionAsync() => await RouteEtsProcessesAsync();

    /// <summary>Developer tool action; routes ETS only and leaves Windows' default untouched.</summary>
    public async Task<string> SetVirtualDefaultAsync()
    {
        LastSetVirtualDefaultSucceeded = false;
        return await RouteEtsProcessesAsync();
    }

    private async Task<string> RouteEtsProcessesAsync()
    {
        await _gate.WaitAsync();
        try
        {
            IReadOnlyCollection<int> processIds = AssistantService.Current.GetEtsProcessIds();
            if (processIds.Count == 0)
            {
                LastSetVirtualDefaultSucceeded = false;
                return LocalizationService.Current.Translate("audio_input.no_ets_process_found");
            }

            await RefreshCaptureDeviceIfNeededAsync();
            if (_captureDevice is null)
            {
                LastSetVirtualDefaultSucceeded = false;
                return LocalizationService.Current.Translate("audio_input.cable_output_missing");
            }

            var currentIds = processIds.ToHashSet();
            foreach (int staleProcessId in _previousRoutes.Keys.Select(key => key.ProcessId).Distinct()
                         .Where(processId => !currentIds.Contains(processId)).ToArray())
                RestoreProcessRoutes(staleProcessId);

            foreach (int processId in currentIds)
            foreach (EndpointRole role in Enum.GetValues<EndpointRole>())
            {
                var routeKey = (processId, role);
                if (!_previousRoutes.ContainsKey(routeKey))
                    _previousRoutes[routeKey] = PerProcessAudioPolicy.GetCaptureEndpoint(processId, role);

                if (!_routedEndpoints.TryGetValue(routeKey, out string? routedEndpoint) ||
                    !string.Equals(routedEndpoint, _captureDevice.Device.Id, StringComparison.OrdinalIgnoreCase))
                {
                    PerProcessAudioPolicy.SetCaptureEndpoint(processId, role, _captureDevice.Device.Id);
                    _routedEndpoints[routeKey] = _captureDevice.Device.Id;
                }
            }

            LastSetVirtualDefaultSucceeded = true;
            DiagnosticLogService.Info("audio_session.ets_capture_routed",
                $"processCount={currentIds.Count} device={_captureDevice.Name} systemDefaultUnchanged=true");
            return LocalizationService.Current.Format("audio_input.ets_capture_routed",
                ("device", _captureDevice.Name), ("count", currentIds.Count));
        }
        catch (Exception ex)
        {
            LastSetVirtualDefaultSucceeded = false;
            DiagnosticLogService.Warn("audio_session.ets_capture_route_failed", ex.Message);
            return LocalizationService.Current.Format("audio_input.ets_capture_route_failed", ("detail", ex.Message));
        }
        finally { _gate.Release(); }
    }

    public async Task<string> RestorePreviousAsync()
    {
        await _gate.WaitAsync();
        try
        {
            RestoreAllProcessRoutes();
            LastSetVirtualDefaultSucceeded = false;
            return LocalizationService.Current.Translate("audio_input.ets_capture_route_restored");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warn("audio_session.ets_capture_route_restore_failed", ex.Message);
            return LocalizationService.Current.Format("audio_input.ets_capture_route_restore_failed", ("detail", ex.Message));
        }
        finally { _gate.Release(); }
    }

    public async Task EndSessionAsync()
    {
        await _gate.WaitAsync();
        try { RestoreAllProcessRoutes(); }
        catch (Exception ex) { DiagnosticLogService.Warn("audio_session.ets_capture_route_restore_failed", ex.Message); }
        finally
        {
            _captureDevice = null;
            _captureDeviceCheckedAt = default;
            LastSetVirtualDefaultSucceeded = false;
            _gate.Release();
        }
    }

    private async Task RefreshCaptureDeviceIfNeededAsync()
    {
        if (_captureDevice is not null && DateTimeOffset.UtcNow - _captureDeviceCheckedAt < TimeSpan.FromSeconds(30))
            return;

        var cable = new VirtualCableService();
        await cable.RefreshAsync();
        _captureDevice = cable.Capture;
        _captureDeviceCheckedAt = DateTimeOffset.UtcNow;
    }

    private void RestoreAllProcessRoutes()
    {
        foreach (int processId in _previousRoutes.Keys.Select(key => key.ProcessId).Distinct().ToArray())
            RestoreProcessRoutes(processId);
    }

    private void RestoreProcessRoutes(int processId)
    {
        foreach (var pair in _previousRoutes.Where(pair => pair.Key.ProcessId == processId).ToArray())
        {
            PerProcessAudioPolicy.SetCaptureEndpoint(processId, pair.Key.Role, pair.Value);
            _previousRoutes.Remove(pair.Key);
            _routedEndpoints.Remove(pair.Key);
        }
    }

    private static bool IsVirtualName(string name) =>
        name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase);

    private enum EndpointRole : uint { Console = 0, Multimedia = 1, Communications = 2 }

    /// <summary>
    /// Windows exposes per-application capture routing through an undocumented WinRT audio-policy
    /// factory. The interface differs by Windows build; failures are surfaced and never fall back
    /// to changing the global default device.
    /// </summary>
    private static class PerProcessAudioPolicy
    {
        private static readonly object FactoryLock = new();
        private static IAudioPolicyConfigFactory? _factory;

        public static string? GetCaptureEndpoint(int processId, EndpointRole role)
        {
            IAudioPolicyConfigFactory factory = GetFactory();
            int result = factory.GetPersistedDefaultAudioEndpoint((uint)processId, AudioDataFlow.Capture, role,
                out string? deviceId);
            if (result >= 0) return string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;

            // Windows uses not-found (and on some builds E_FAIL) to mean no per-app override exists.
            if (result is unchecked((int)0x80070490) or unchecked((int)0x80004005)) return null;
            Marshal.ThrowExceptionForHR(result);
            return null;
        }

        public static void SetCaptureEndpoint(int processId, EndpointRole role, string? deviceId)
        {
            IAudioPolicyConfigFactory factory = GetFactory();
            IntPtr deviceString = IntPtr.Zero;
            try
            {
                if (deviceId is not null)
                {
                    Marshal.ThrowExceptionForHR(WindowsCreateString(deviceId, (uint)deviceId.Length, out deviceString));
                }

                Marshal.ThrowExceptionForHR(factory.SetPersistedDefaultAudioEndpoint((uint)processId,
                    AudioDataFlow.Capture, role, deviceString));
            }
            finally
            {
                if (deviceString != IntPtr.Zero) WindowsDeleteString(deviceString);
            }
        }

        private static IAudioPolicyConfigFactory GetFactory()
        {
            lock (FactoryLock)
            {
                if (_factory is not null) return _factory;

                string className = "Windows.Media.Internal.AudioPolicyConfig";
                Marshal.ThrowExceptionForHR(WindowsCreateString(className, (uint)className.Length, out IntPtr classNameString));
                try
                {
                    Guid iid = Environment.OSVersion.Version.Build >= 19044
                        ? new Guid("ab3d4648-e242-459f-b02f-541c70306324")
                        : new Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258");
                    Marshal.ThrowExceptionForHR(RoGetActivationFactory(classNameString, ref iid, out IntPtr factoryPointer));
                    try
                    {
                        bool downlevel = Environment.OSVersion.Version.Build < 19044;
                        _factory = new AudioPolicyConfigFactory(Marshal.GetObjectForIUnknown(factoryPointer), downlevel);
                        return _factory;
                    }
                    finally { Marshal.Release(factoryPointer); }
                }
                finally { WindowsDeleteString(classNameString); }
            }
        }

        [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString,
            uint length, out IntPtr value);

        [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WindowsDeleteString(IntPtr value);

        [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid,
            out IntPtr factory);

        private enum AudioDataFlow : int { Render = 0, Capture = 1, All = 2 }

        private interface IAudioPolicyConfigFactory
        {
            int SetPersistedDefaultAudioEndpoint(uint processId, AudioDataFlow flow, EndpointRole role, IntPtr deviceId);
            int GetPersistedDefaultAudioEndpoint(uint processId, AudioDataFlow flow, EndpointRole role, out string? deviceId);
        }

        private sealed class AudioPolicyConfigFactory(object factory, bool downlevel) : IAudioPolicyConfigFactory
        {
            public int SetPersistedDefaultAudioEndpoint(uint processId, AudioDataFlow flow,
                EndpointRole role, IntPtr deviceId) => downlevel
                ? ((IAudioPolicyConfigFactoryDownlevel)factory).SetPersistedDefaultAudioEndpoint(processId, flow, role, deviceId)
                : ((IAudioPolicyConfigFactory21H2)factory).SetPersistedDefaultAudioEndpoint(processId, flow, role, deviceId);

            public int GetPersistedDefaultAudioEndpoint(uint processId, AudioDataFlow flow,
                EndpointRole role, out string? deviceId)
            {
                if (downlevel)
                    return ((IAudioPolicyConfigFactoryDownlevel)factory).GetPersistedDefaultAudioEndpoint(processId, flow, role, out deviceId);
                return ((IAudioPolicyConfigFactory21H2)factory).GetPersistedDefaultAudioEndpoint(processId, flow, role, out deviceId);
            }
        }

        [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioPolicyConfigFactory21H2
        {
            // IInspectable's three ABI slots. Declaring the interface as IUnknown avoids
            // .NET's unsupported IInspectable marshaller while preserving the WinRT vtable.
            [PreserveSig] int GetIids(out uint iidCount, out IntPtr iids);
            [PreserveSig] int GetRuntimeClassName(out IntPtr className);
            [PreserveSig] int GetTrustLevel(out int trustLevel);
            int __incomplete__add_CtxVolumeChange();
            int __incomplete__remove_CtxVolumeChanged();
            int __incomplete__add_RingerVibrateStateChanged();
            int __incomplete__remove_RingerVibrateStateChange();
            int __incomplete__SetVolumeGroupGainForId();
            int __incomplete__GetVolumeGroupGainForId();
            int __incomplete__GetActiveVolumeGroupForEndpointId();
            int __incomplete__GetVolumeGroupsForEndpoint();
            int __incomplete__GetCurrentVolumeContext();
            int __incomplete__SetVolumeGroupMuteForId();
            int __incomplete__GetVolumeGroupMuteForId();
            int __incomplete__SetRingerVibrateState();
            int __incomplete__GetRingerVibrateState();
            int __incomplete__SetPreferredChatApplication();
            int __incomplete__ResetPreferredChatApplication();
            int __incomplete__GetPreferredChatApplication();
            int __incomplete__GetCurrentChatApplications();
            int __incomplete__add_ChatContextChanged();
            int __incomplete__remove_ChatContextChanged();
            [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, AudioDataFlow flow,
                EndpointRole role, IntPtr deviceId);
            [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, AudioDataFlow flow,
                EndpointRole role, [Out, MarshalAs(UnmanagedType.HString)] out string? deviceId);
            [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
        }

        [ComImport, Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioPolicyConfigFactoryDownlevel
        {
            // IInspectable's three ABI slots. Keep these before the factory methods below.
            [PreserveSig] int GetIids(out uint iidCount, out IntPtr iids);
            [PreserveSig] int GetRuntimeClassName(out IntPtr className);
            [PreserveSig] int GetTrustLevel(out int trustLevel);
            int __incomplete__add_CtxVolumeChange();
            int __incomplete__remove_CtxVolumeChanged();
            int __incomplete__add_RingerVibrateStateChanged();
            int __incomplete__remove_RingerVibrateStateChange();
            int __incomplete__SetVolumeGroupGainForId();
            int __incomplete__GetVolumeGroupGainForId();
            int __incomplete__GetActiveVolumeGroupForEndpointId();
            int __incomplete__GetVolumeGroupsForEndpoint();
            int __incomplete__GetCurrentVolumeContext();
            int __incomplete__SetVolumeGroupMuteForId();
            int __incomplete__GetVolumeGroupMuteForId();
            int __incomplete__SetRingerVibrateState();
            int __incomplete__GetRingerVibrateState();
            int __incomplete__SetPreferredChatApplication();
            int __incomplete__ResetPreferredChatApplication();
            int __incomplete__GetPreferredChatApplication();
            int __incomplete__GetCurrentChatApplications();
            int __incomplete__add_ChatContextChanged();
            int __incomplete__remove_ChatContextChanged();
            [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, AudioDataFlow flow,
                EndpointRole role, IntPtr deviceId);
            [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, AudioDataFlow flow,
                EndpointRole role, [Out, MarshalAs(UnmanagedType.HString)] out string? deviceId);
            [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
        }
    }
}
