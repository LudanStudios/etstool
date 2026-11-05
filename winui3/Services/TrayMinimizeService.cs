using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using ETSAssistant.WinUI3.Services;

namespace ETSAssistant.WinUI3.Services;

/// <summary>Moves the main window to the notification area when minimized.</summary>
internal sealed class TrayMinimizeService : IDisposable
{
    private const uint WmSize = 0x0005;
    private const uint WmApp = 0x8000;
    private const uint WmTrayCallback = WmApp + 0x52;
    private const uint WmShowTrayStatus = WmApp + 0x53;
    private const int SizeMinimized = 1;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int SwHide = 0;
    private const int SwRestore = 9;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 1;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x10;
    private static readonly nuint SubclassId = 0x45545354;
    private static readonly SubclassProc Callback = WindowSubclassProc;

    private readonly nint _hwnd;
    private readonly nint _icon;
    private readonly DispatcherQueueTimer _leftClickTimer;
    private readonly DispatcherQueue _queue;
    private bool _iconAdded;
    private bool _disposed;
    private long _ignoreClickUntil;

    public TrayMinimizeService(nint hwnd)
    {
        _hwnd = hwnd;
        DispatcherQueue queue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("当前线程没有可用的 DispatcherQueue。");
        _queue = queue;
        _leftClickTimer = queue.CreateTimer();
        _leftClickTimer.IsRepeating = false;
        _leftClickTimer.Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime());
        _leftClickTimer.Tick += (_, _) => MainWindow.Current?.ShowTrayStatusFlyout();
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ProgramIcon.ico");
        _icon = LoadImageW(0, iconPath, ImageIcon, 0, 0, LrLoadFromFile);
        if (_icon == 0)
            DiagnosticLogService.Warn("tray.icon.load.failed", $"path={iconPath}");
        if (!SetWindowSubclass(_hwnd, Callback, SubclassId, 0))
            DiagnosticLogService.Warn("tray.window_subclass.failed", $"error={Marshal.GetLastWin32Error()}");
    }

    private static nint WindowSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam,
        nuint subclassId, nuint referenceData)
    {
        TrayMinimizeService? service = MainWindow.Current?.TrayService;
        if (service is not null && hwnd == service._hwnd)
        {
            if (message == WmShowTrayStatus)
            {
                DiagnosticLogService.Info("tray.status.show_request.dispatched");
                MainWindow.Current?.ShowTrayStatusFlyout();
                return 0;
            }

            if (message == WmSize && (int)wParam == SizeMinimized &&
                AppPreferencesService.Current.MinimizeToTrayEnabled)
            {
                service.MoveToTray();
                return 0;
            }

            int trayEvent = message == WmTrayCallback ? GetTrayEvent(lParam) : 0;
            if (message == WmTrayCallback && trayEvent == WmLButtonDblClk)
            {
                service._leftClickTimer.Stop();
                service._ignoreClickUntil = Environment.TickCount64 + GetDoubleClickTime();
                service.RestoreFromTray();
                return 0;
            }

            if (message == WmTrayCallback && trayEvent is WmLButtonUp or 0x0400 or 0x0401)
            {
                if (Environment.TickCount64 < service._ignoreClickUntil) return 0;
                service._leftClickTimer.Stop();
                service._leftClickTimer.Start();
                return 0;
            }

            if (message == WmTrayCallback && trayEvent is WmRButtonUp or 0x007B)
            {
                service._leftClickTimer.Stop();
                DiagnosticLogService.Info("tray.status.right_click.received");
                // Dispatch directly to the UI queue: the main HWND is hidden in tray mode.
                service._queue.TryEnqueue(() => MainWindow.Current?.ShowTrayStatusFlyout());
                return 0;
            }
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private static int GetTrayEvent(nint lParam) => unchecked((ushort)lParam.ToInt64());

    private void MoveToTray()
    {
        if (_disposed || _icon == 0) return;
        if (!_iconAdded)
        {
            var data = CreateNotifyData();
            if (!Shell_NotifyIconW(NimAdd, ref data))
            {
                DiagnosticLogService.Warn("tray.icon.add.failed", $"error={Marshal.GetLastWin32Error()}");
                return;
            }
            _iconAdded = true;
            data.TimeoutOrVersion = 4;
            Shell_NotifyIconW(NimSetVersion, ref data);
            DiagnosticLogService.Info("tray.window.minimized_to_tray");
        }

        ShowWindow(_hwnd, SwHide);
    }

    private void RestoreFromTray()
    {
        if (_disposed) return;
        MainWindow.Current?.HideTrayStatusFlyout();
        RemoveIcon();
        ShowWindow(_hwnd, SwRestore);
        SetForegroundWindow(_hwnd);
        DiagnosticLogService.Info("tray.window.restored");
    }

    private NotifyIconData CreateNotifyData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _hwnd,
        Id = 1,
        Flags = NifMessage | NifIcon | NifTip,
        CallbackMessage = WmTrayCallback,
        Icon = _icon,
        Tip = "ETS Tool",
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    private void RemoveIcon()
    {
        if (!_iconAdded) return;
        var data = CreateNotifyData();
        Shell_NotifyIconW(NimDelete, ref data);
        _iconAdded = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _leftClickTimer.Stop();
        RemoveIcon();
        RemoveWindowSubclass(_hwnd, Callback, SubclassId);
        if (_icon != 0) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIcon;
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam,
        nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
}
