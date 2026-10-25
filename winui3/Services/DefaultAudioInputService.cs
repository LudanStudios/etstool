using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace ETSAssistant.WinUI3.Services;

public sealed record DefaultAudioInputStatus(string DeviceId, string Name, bool IsVirtualCable);

/// <summary>Temporarily selects CABLE Output for capture and restores the previous Windows default on exit.</summary>
public sealed class DefaultAudioInputService
{
    private static readonly Lazy<DefaultAudioInputService> Lazy = new(() => new DefaultAudioInputService());
    public static DefaultAudioInputService Current => Lazy.Value;
    private string? _originalDeviceId;
    private string? _originalRenderDeviceId;
    private string? _virtualDeviceId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<DefaultAudioInputStatus?> GetStatusAsync()
    {
        string id = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
        if (string.IsNullOrWhiteSpace(id)) return null;
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
        DeviceInformation? match = devices.FirstOrDefault(device => string.Equals(device.Id, id,
            StringComparison.OrdinalIgnoreCase));
        string name = match?.Name ?? id;
        return new DefaultAudioInputStatus(id, name,
            IsVirtualName(name));
    }

    public async Task<string> BeginSessionAsync()
    {
        await _gate.WaitAsync();
        try
        {
            DefaultAudioInputStatus? current = await GetStatusAsync();
            if (current is null) return "未能读取 Windows 默认录音设备。";
            _originalDeviceId ??= current.IsVirtualCable
                ? await FindPhysicalFallbackAsync()
                : current.DeviceId;
            AudioEndpointStatus? render = await GetDefaultRenderStatusAsync();
            if (render is not null)
                _originalRenderDeviceId ??= render.IsVirtualCable
                    ? await FindPhysicalRenderFallbackAsync()
                    : render.DeviceId;
            VirtualCableService cable = new();
            await cable.RefreshAsync();
            if (cable.Capture is null) return $"当前默认输入：{current.Name}；未检测到 CABLE Output。";
            _virtualDeviceId = cable.Capture.Device.Id;
            _originalDeviceId ??= string.Equals(current.DeviceId, _virtualDeviceId, StringComparison.OrdinalIgnoreCase)
                ? null : current.DeviceId;
            SetDefaultEndpoint(_virtualDeviceId);
            return $"已将默认输入临时切换为：{cable.Capture.Name}";
        }
        catch (Exception ex) { return "虚拟麦克风切换失败：" + ex.Message; }
        finally { _gate.Release(); }
    }

    public async Task<string> SetVirtualDefaultAsync()
    {
        await _gate.WaitAsync();
        try
        {
            DefaultAudioInputStatus? current = await GetStatusAsync();
            if (current is null) return "未能读取 Windows 默认录音设备。";
            VirtualCableService cable = new();
            await cable.RefreshAsync();
            if (cable.Capture is null) return "未检测到 CABLE Output 录音设备。";
            if (!current.IsVirtualCable) _originalDeviceId ??= current.DeviceId;
            _virtualDeviceId = cable.Capture.Device.Id;
            SetDefaultEndpoint(_virtualDeviceId);
            return $"已设为默认输入：{cable.Capture.Name}";
        }
        catch (Exception ex) { return "设置失败：" + ex.Message; }
        finally { _gate.Release(); }
    }

    public async Task<string> RestorePreviousAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (string.IsNullOrWhiteSpace(_originalDeviceId)) return "本次会话没有保存可恢复的物理麦克风。";
            SetDefaultEndpoint(_originalDeviceId);
            DefaultAudioInputStatus? status = await GetStatusAsync();
            return "已恢复默认输入：" + (status?.Name ?? _originalDeviceId);
        }
        catch (Exception ex) { return "恢复失败：" + ex.Message; }
        finally { _gate.Release(); }
    }

    public async Task EndSessionAsync()
    {
        await _gate.WaitAsync();
        try
        {
            DefaultAudioInputStatus? current = await GetStatusAsync();
            if (!string.IsNullOrWhiteSpace(_originalDeviceId) &&
                !string.IsNullOrWhiteSpace(_virtualDeviceId) && current is not null &&
                string.Equals(current.DeviceId, _virtualDeviceId, StringComparison.OrdinalIgnoreCase))
                SetDefaultEndpoint(_originalDeviceId);

            AudioEndpointStatus? render = await GetDefaultRenderStatusAsync();
            if (!string.IsNullOrWhiteSpace(_originalRenderDeviceId) && render?.IsVirtualCable == true)
                SetDefaultEndpoint(_originalRenderDeviceId);
        }
        catch (Exception) { }
        finally
        {
            _originalDeviceId = null;
            _originalRenderDeviceId = null;
            _virtualDeviceId = null;
            _gate.Release();
        }
    }

    private static void SetDefaultEndpoint(string deviceId)
    {
        IPolicyConfig? policy = null;
        try
        {
            policy = (IPolicyConfig)new PolicyConfigClient();
            foreach (EndpointRole role in Enum.GetValues<EndpointRole>())
            {
                int result = policy.SetDefaultEndpoint(deviceId, role);
                Marshal.ThrowExceptionForHR(result);
            }
        }
        finally
        {
            if (policy is not null && Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy);
        }
    }

    private static async Task<string?> FindPhysicalFallbackAsync()
    {
        string communications = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Communications);
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
        DeviceInformation? communicationDevice = devices.FirstOrDefault(device =>
            string.Equals(device.Id, communications, StringComparison.OrdinalIgnoreCase));
        if (communicationDevice is not null && !IsVirtualName(communicationDevice.Name))
            return communicationDevice.Id;
        return devices.FirstOrDefault(device =>
            !IsVirtualName(device.Name))?.Id;
    }

    private sealed record AudioEndpointStatus(string DeviceId, string Name, bool IsVirtualCable);

    private static async Task<AudioEndpointStatus?> GetDefaultRenderStatusAsync()
    {
        string id = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
        if (string.IsNullOrWhiteSpace(id)) return null;
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioRender);
        DeviceInformation? match = devices.FirstOrDefault(device => string.Equals(device.Id, id,
            StringComparison.OrdinalIgnoreCase));
        string name = match?.Name ?? id;
        return new AudioEndpointStatus(id, name, IsVirtualName(name));
    }

    private static async Task<string?> FindPhysicalRenderFallbackAsync()
    {
        string communications = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Communications);
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioRender);
        DeviceInformation? communicationDevice = devices.FirstOrDefault(device =>
            string.Equals(device.Id, communications, StringComparison.OrdinalIgnoreCase));
        if (communicationDevice is not null && !IsVirtualName(communicationDevice.Name))
            return communicationDevice.Id;
        return devices.FirstOrDefault(device => !IsVirtualName(device.Name))?.Id;
    }

    private static bool IsVirtualName(string name) =>
        name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase);

    private enum EndpointRole { Console = 0, Multimedia = 1, Communications = 2 }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr shareMode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr shareMode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, EndpointRole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
