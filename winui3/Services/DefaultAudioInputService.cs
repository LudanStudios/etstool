using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace ETSAssistant.WinUI3.Services;

public sealed record DefaultAudioInputStatus(string DeviceId, string Name, bool IsVirtualCable);

/// <summary>
/// Routes ETS capture to the virtual cable while preserving normal ETS playback.
/// It never changes either machine-wide default endpoint.
/// </summary>
public sealed class DefaultAudioInputService
{
    private static readonly Lazy<DefaultAudioInputService> Lazy = new(() => new DefaultAudioInputService());
    public static DefaultAudioInputService Current => Lazy.Value;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<RouteKey, string?> _previousRoutes = new();
    private readonly Dictionary<RouteKey, string> _routedEndpoints = new();
    private VirtualCableDevice? _captureDevice;
    private VirtualCableDevice? _renderDevice;
    private DateTimeOffset _devicesCheckedAt;

    public bool LastEtsAudioRouteSucceeded { get; private set; }
    public bool HasActiveEtsRoute => _routedEndpoints.Count > 0;
    public string? RoutedDeviceName => _captureDevice?.Name;
    public string? RoutedPlaybackDeviceName => _renderDevice?.Name;

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

    /// <summary>Applies the virtual microphone only to currently running ETS processes.</summary>
    public async Task<string> BeginSessionAsync() => await RouteEtsProcessesAsync();

    /// <summary>Developer tool action; routes ETS only and leaves Windows' defaults untouched.</summary>
    public async Task<string> SetVirtualDefaultAsync()
    {
        LastEtsAudioRouteSucceeded = false;
        return await RouteEtsProcessesAsync();
    }

    private async Task<string> RouteEtsProcessesAsync()
    {
        LastEtsAudioRouteSucceeded = false;
        await _gate.WaitAsync();
        try
        {
            IReadOnlyCollection<int> processIds = AssistantService.Current.GetEtsProcessIds();
            if (processIds.Count == 0)
            {
                LastEtsAudioRouteSucceeded = false;
                return LocalizationService.Current.Translate("audio_input.no_ets_process_found");
            }

            await RefreshDevicesIfNeededAsync();
            if (_captureDevice is null || _renderDevice is null)
            {
                LastEtsAudioRouteSucceeded = false;
                return LocalizationService.Current.Translate("audio_input.cable_devices_missing");
            }

            var runningIds = processIds.ToHashSet();
            foreach (int staleProcessId in _previousRoutes.Keys.Select(key => key.ProcessId).Distinct()
                         .Where(processId => !runningIds.Contains(processId)).ToArray())
                RestoreProcessRoutes(staleProcessId);

            // Chromium-based ETS starts helper processes that have no audio session. Windows
            // reports E_INVALIDARG for those PIDs; they must not block audio-capable processes.
            var currentIds = new HashSet<int>();
            foreach (int processId in runningIds)
            {
                try
                {
                    PerProcessAudioPolicy.GetEndpoint(processId, EndpointFlow.Capture, EndpointRole.Console);
                    currentIds.Add(processId);
                }
                catch (COMException ex) when (ex.HResult == unchecked((int)0x80070057))
                {
                    DiagnosticLogService.Info("audio_session.process_without_audio", $"pid={processId}");
                }
            }
            if (currentIds.Count == 0)
                throw new InvalidOperationException("ETS audio process is not ready (0x80070057).");

            var changedThisAttempt = new List<(RouteKey Key, string? PreviousEndpoint, bool WasTracked)>();
            try
            {
                foreach (int processId in currentIds)
                foreach (EndpointRole role in Enum.GetValues<EndpointRole>())
                {
                    // Older builds routed ETS playback into CABLE Input, making its
                    // prompts and recording playback inaudible. Remove only that
                    // specific legacy override and leave other user choices intact.
                    string? previousRender = PerProcessAudioPolicy.GetEndpoint(processId, EndpointFlow.Render, role);
                    if (string.Equals(previousRender, _renderDevice.Device.Id, StringComparison.OrdinalIgnoreCase))
                        PerProcessAudioPolicy.SetEndpoint(processId, EndpointFlow.Render, role, null);
                    EndpointFlow flow = EndpointFlow.Capture;
                    RouteKey routeKey = new(processId, flow, role);
                    string targetEndpoint = _captureDevice.Device.Id;

                    bool wasTracked = _previousRoutes.ContainsKey(routeKey);
                    if (!wasTracked)
                        _previousRoutes[routeKey] = PerProcessAudioPolicy.GetEndpoint(processId, flow, role);

                    string? currentEndpoint = PerProcessAudioPolicy.GetEndpoint(processId, flow, role);
                    if (!string.Equals(currentEndpoint, targetEndpoint, StringComparison.OrdinalIgnoreCase))
                    {
                        changedThisAttempt.Add((routeKey, currentEndpoint, wasTracked));
                        PerProcessAudioPolicy.SetEndpoint(processId, flow, role, targetEndpoint);

                        string? confirmedEndpoint = PerProcessAudioPolicy.GetEndpoint(processId, flow, role);
                        if (!string.Equals(confirmedEndpoint, targetEndpoint, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(LocalizationService.Current.Format(
                                "audio_input.endpoint_route_not_confirmed",
                                ("flow", flow.ToString()), ("role", role.ToString()),
                                ("expected", targetEndpoint), ("actual", confirmedEndpoint ?? "null")));
                        }
                    }

                    _routedEndpoints[routeKey] = targetEndpoint;
                }
            }
            catch
            {
                foreach (var change in changedThisAttempt.AsEnumerable().Reverse())
                {
                    try
                    {
                        PerProcessAudioPolicy.SetEndpoint(change.Key.ProcessId, change.Key.Flow,
                            change.Key.Role, change.PreviousEndpoint);
                    }
                    catch (Exception rollbackError)
                    {
                        DiagnosticLogService.Warn("audio_session.route_rollback_failed",
                            $"pid={change.Key.ProcessId}; flow={change.Key.Flow}; {rollbackError.Message}");
                    }

                    _routedEndpoints.Remove(change.Key);
                    if (!change.WasTracked) _previousRoutes.Remove(change.Key);
                }
                throw;
            }

            LastEtsAudioRouteSucceeded = true;
            DiagnosticLogService.Info("audio_session.ets_audio_routed",
                $"processCount={currentIds.Count} capture={_captureDevice.Name} playerOutput={_renderDevice.Name} etsPlayback=unchanged systemDefaultsUnchanged=true");
            return LocalizationService.Current.Format("audio_input.ets_audio_routed",
                ("captureDevice", _captureDevice.Name), ("renderDevice", _renderDevice.Name),
                ("count", currentIds.Count));
        }
        catch (Exception ex)
        {
            LastEtsAudioRouteSucceeded = false;
            DiagnosticLogService.Warn("audio_session.ets_audio_route_failed", ex.Message);
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
            LastEtsAudioRouteSucceeded = false;
            return LocalizationService.Current.Translate("audio_input.ets_capture_route_restored");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warn("audio_session.ets_audio_route_restore_failed", ex.Message);
            return LocalizationService.Current.Format("audio_input.ets_capture_route_restore_failed", ("detail", ex.Message));
        }
        finally { _gate.Release(); }
    }

    public async Task EndSessionAsync()
    {
        await _gate.WaitAsync();
        try { RestoreAllProcessRoutes(); }
        catch (Exception ex) { DiagnosticLogService.Warn("audio_session.ets_audio_route_restore_failed", ex.Message); }
        finally
        {
            _captureDevice = null;
            _renderDevice = null;
            _devicesCheckedAt = default;
            LastEtsAudioRouteSucceeded = false;
            _gate.Release();
        }
    }

    private async Task RefreshDevicesIfNeededAsync()
    {
        if (_captureDevice is not null && _renderDevice is not null &&
            DateTimeOffset.UtcNow - _devicesCheckedAt < TimeSpan.FromSeconds(30))
            return;

        var cable = new VirtualCableService();
        await cable.RefreshAsync();
        _renderDevice = cable.Current;
        _captureDevice = cable.Capture;
        _devicesCheckedAt = DateTimeOffset.UtcNow;
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
            PerProcessAudioPolicy.SetEndpoint(processId, pair.Key.Flow, pair.Key.Role, pair.Value);
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

    private readonly record struct RouteKey(int ProcessId, EndpointFlow Flow, EndpointRole Role);
    private enum EndpointFlow : int { Render = 0, Capture = 1 }
    private enum EndpointRole : uint { Console = 0, Multimedia = 1, Communications = 2 }

    /// <summary>
    /// Calls the undocumented per-process Windows audio policy ABI through raw vtable delegates.
    /// Avoids .NET WinRT/ IInspectable RCW marshalling, which is unsupported by this self-contained
    /// .NET runtime. The IIDs differ before and after Windows 10 21H2.
    /// </summary>
    private static class PerProcessAudioPolicy
    {
        private const int SetEndpointVtableSlot = 25;
        private const int GetEndpointVtableSlot = 26;
        private static readonly object FactoryLock = new();
        // Keep the single activation-factory reference for the process lifetime.
        private static IntPtr _factoryPointer;

        public static string? GetEndpoint(int processId, EndpointFlow flow, EndpointRole role)
        {
            IntPtr hstring = IntPtr.Zero;
            int result = GetGetEndpointDelegate()(GetFactoryPointer(), (uint)processId,
                (int)flow, (int)role, out hstring);
            try
            {
                if (result >= 0)
                {
                    IntPtr buffer = WindowsGetStringRawBuffer(hstring, out uint length);
                    return length == 0 ? null : Marshal.PtrToStringUni(buffer, checked((int)length));
                }

                // Windows uses not-found (and on some builds E_FAIL) when no app override exists.
                if (result is unchecked((int)0x80070490) or unchecked((int)0x80004005)) return null;
                throw new COMException(
                    $"GetPersistedDefaultAudioEndpoint failed (pid={processId}, flow={flow}, role={role}, HRESULT=0x{result:X8}).",
                    result);
            }
            finally
            {
                if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
            }
        }

        public static void SetEndpoint(int processId, EndpointFlow flow, EndpointRole role, string? deviceId)
        {
            // This WinRT ABI takes an HSTRING handle, including when invoked through the raw vtable.
            IntPtr deviceIdPointer = IntPtr.Zero;
            try
            {
                if (deviceId is not null)
                    Marshal.ThrowExceptionForHR(WindowsCreateString(deviceId, (uint)deviceId.Length, out deviceIdPointer));

                int result = GetSetEndpointDelegate()(GetFactoryPointer(), (uint)processId,
                    (int)flow, (int)role, deviceIdPointer);
                if (result < 0)
                {
                    throw new COMException(
                        $"SetPersistedDefaultAudioEndpoint failed (pid={processId}, flow={flow}, role={role}, HRESULT=0x{result:X8}).",
                        result);
                }
            }
            finally
            {
                if (deviceIdPointer != IntPtr.Zero) WindowsDeleteString(deviceIdPointer);
            }
        }

        private static IntPtr GetFactoryPointer()
        {
            lock (FactoryLock)
            {
                if (_factoryPointer != IntPtr.Zero) return _factoryPointer;

                string className = "Windows.Media.Internal.AudioPolicyConfig";
                Marshal.ThrowExceptionForHR(WindowsCreateString(className, (uint)className.Length,
                    out IntPtr classNameString));
                try
                {
                    Guid iid = Environment.OSVersion.Version.Build >= 19044
                        ? new Guid("ab3d4648-e242-459f-b02f-541c70306324")
                        : new Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258");
                    Marshal.ThrowExceptionForHR(RoGetActivationFactory(classNameString, ref iid,
                        out _factoryPointer));
                    return _factoryPointer;
                }
                finally { WindowsDeleteString(classNameString); }
            }
        }

        private static SetEndpointDelegate GetSetEndpointDelegate()
        {
            IntPtr vtable = Marshal.ReadIntPtr(GetFactoryPointer());
            IntPtr method = Marshal.ReadIntPtr(vtable, SetEndpointVtableSlot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<SetEndpointDelegate>(method);
        }

        private static GetEndpointDelegate GetGetEndpointDelegate()
        {
            IntPtr vtable = Marshal.ReadIntPtr(GetFactoryPointer());
            IntPtr method = Marshal.ReadIntPtr(vtable, GetEndpointVtableSlot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<GetEndpointDelegate>(method);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetEndpointDelegate(IntPtr self, uint processId, int flow,
            int role, IntPtr deviceId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetEndpointDelegate(IntPtr self, uint processId, int flow,
            int role, out IntPtr deviceId);

        [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString,
            uint length, out IntPtr value);

        [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WindowsDeleteString(IntPtr value);

        [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern IntPtr WindowsGetStringRawBuffer(IntPtr value, out uint length);

        [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid,
            out IntPtr factory);
    }
}
