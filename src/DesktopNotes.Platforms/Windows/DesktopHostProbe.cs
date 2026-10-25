using System.Runtime.InteropServices;
using System.Text;
using DesktopNotes.Application.Contracts;
using Microsoft.Win32;

namespace DesktopNotes.Platforms.Windows;

public sealed record DesktopHostDiscoveryResult(
    bool Found,
    nint ProgmanHandle,
    nint DefViewParentHandle,
    nint DefViewHandle,
    string HostClassName,
    string DiagnosticSummary);

public sealed class DesktopHostProbe : IDesktopIntegration
{
    private const int GWL_EXSTYLE = -20;
    private const int GWL_STYLE = -16;
    private const nint WS_EX_TOOLWINDOW = 0x00000080;
    private const nint WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private static readonly nint HWND_TOPMOST = new(-1);
    private static readonly nint HWND_NOTOPMOST = new(-2);
    private static readonly nint HWND_BOTTOM = new(1);
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "DesktopNotes";

    private nint _attachedWindow;
    private nint _previousOwner;
    private PlacementMode _currentMode = PlacementMode.Topmost;

    public PlacementMode CurrentMode => _currentMode;
    public DesktopCapabilities Capabilities { get; private set; }

    public DesktopHostProbe()
    {
        if (!OperatingSystem.IsWindows())
        {
            Capabilities = new DesktopCapabilities(
                CapabilitySupport.Unknown,
                CapabilitySupport.Unknown,
                CapabilitySupport.Unknown,
                CapabilitySupport.Unknown,
                $"Non-Windows runtime ({RuntimeInformation.OSDescription}): platform integration not verified on this OS.");
            return;
        }

        var discovery = ProbeDesktopHost();
        Capabilities = new DesktopCapabilities(
            discovery.Found ? CapabilitySupport.Supported : CapabilitySupport.Unsupported,
            CapabilitySupport.Supported,
            CapabilitySupport.Supported,
            CapabilitySupport.Supported,
            discovery.DiagnosticSummary);
    }

    public DesktopHostDiscoveryResult ProbeDesktopHost()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DesktopHostDiscoveryResult(
                false,
                nint.Zero,
                nint.Zero,
                nint.Zero,
                string.Empty,
                "Not running on Windows.");
        }

        nint progman = FindWindowW("Progman", null);
        if (progman == nint.Zero)
        {
            progman = GetShellWindow();
        }
        if (progman == nint.Zero)
        {
            progman = GetDesktopWindow();
        }
        nint defViewParent = nint.Zero;
        nint defView = nint.Zero;

        if (progman != nint.Zero)
        {
            defView = FindWindowExW(progman, nint.Zero, "SHELLDLL_DefView", null);
            if (defView != nint.Zero)
            {
                defViewParent = progman;
            }
        }

        if (defView == nint.Zero)
        {
            EnumWindows((hwnd, _) =>
            {
                nint child = FindWindowExW(hwnd, nint.Zero, "SHELLDLL_DefView", null);
                if (child != nint.Zero)
                {
                    defViewParent = hwnd;
                    defView = child;
                    return false;
                }
                return true;
            }, nint.Zero);
        }

        if (defView == nint.Zero && progman != nint.Zero)
        {
            // On Windows 11 24H2 or restricted window stations, Progman itself is the desktop host
            // and SHELLDLL_DefView may be a deeper child or hosted directly under Progman/WorkerW.
            EnumChildWindows(progman, (child, _) =>
            {
                if (GetWindowClass(child) == "SHELLDLL_DefView")
                {
                    defView = child;
                    defViewParent = GetParent(child);
                    if (defViewParent == nint.Zero) defViewParent = progman;
                    return false;
                }
                return true;
            }, nint.Zero);

            if (defViewParent == nint.Zero)
            {
                defViewParent = progman;
            }
        }

        string hostClass = defViewParent != nint.Zero ? GetWindowClass(defViewParent) : string.Empty;
        bool found = progman != nint.Zero && defViewParent != nint.Zero;
        string summary = found
            ? $"Windows host ready: Progman=0x{progman:X}, DefViewParent=0x{defViewParent:X} ({hostClass}), SHELLDLL_DefView=0x{defView:X}"
            : $"Windows host incomplete: Progman=0x{progman:X}, SHELLDLL_DefView=0x{defView:X}";

        return new DesktopHostDiscoveryResult(found, progman, defViewParent, defView, hostClass, summary);
    }

    public Task ApplyModeAsync(nint windowHandle, PlacementMode mode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || windowHandle == nint.Zero)
        {
            return Task.CompletedTask;
        }

        if (mode == PlacementMode.Topmost)
        {
            DetachFromDesktopHost(windowHandle);
            SetWindowPos(
                windowHandle,
                HWND_TOPMOST,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
            _currentMode = PlacementMode.Topmost;
            return Task.CompletedTask;
        }

        var discovery = ProbeDesktopHost();
        if (!discovery.Found)
        {
            throw new InvalidOperationException($"Cannot attach to desktop host: {discovery.DiagnosticSummary}");
        }

        // Release topmost first, then associate owner with the desktop host container
        // so that Show Desktop (Win+D) keeps the panel visible without stealing focus or breaking OLE drag-drop/IME.
        SetWindowPos(
            windowHandle,
            HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        nint targetHost = discovery.DefViewParentHandle != nint.Zero
            ? discovery.DefViewParentHandle
            : discovery.ProgmanHandle;

        _previousOwner = SetWindowLongPtr(windowHandle, -8 /* GWLP_HWNDPARENT */, targetHost);
        _attachedWindow = windowHandle;

        SetWindowPos(
            windowHandle,
            HWND_BOTTOM,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);

        _currentMode = PlacementMode.EmbeddedDesktop;
        return Task.CompletedTask;
    }

    public void DetachFromDesktopHost(nint windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == nint.Zero)
        {
            return;
        }

        if (_attachedWindow == windowHandle)
        {
            SetWindowLongPtr(windowHandle, -8 /* GWLP_HWNDPARENT */, _previousOwner);
            _attachedWindow = nint.Zero;
            _previousOwner = nint.Zero;
        }
    }

    public Task SetLoginStartupAsync(bool enabled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true);
        if (key is null)
        {
            return Task.CompletedTask;
        }

        if (enabled)
        {
            string exePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
            key.SetValue(StartupValueName, $"\"{exePath}\"");
        }
        else if (key.GetValue(StartupValueName) is not null)
        {
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }

        return Task.CompletedTask;
    }

    public Task<bool> GetLoginStartupAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(false);
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: false);
        object? val = key?.GetValue(StartupValueName);
        return Task.FromResult(val is string s && !string.IsNullOrWhiteSpace(s));
    }

    public WindowPlacement FitToAvailableScreens(WindowPlacement saved)
    {
        double width = Math.Clamp(saved.Width, 280, 1200);
        double height = Math.Clamp(saved.Height, 360, 1400);
        double scale = saved.Scale <= 0 ? 1.0 : saved.Scale;

        double screenWidth = 1920;
        double screenHeight = 1080;
        if (OperatingSystem.IsWindows())
        {
            int w = GetSystemMetrics(0 /* SM_CXSCREEN */);
            int h = GetSystemMetrics(1 /* SM_CYSCREEN */);
            if (w > 0 && h > 0)
            {
                screenWidth = w / scale;
                screenHeight = h / scale;
            }
        }

        double x = saved.X;
        double y = saved.Y;
        if (x < 0 || x + 80 > screenWidth)
        {
            x = Math.Max(24, screenWidth - width - 32);
        }

        if (y < 0 || y + 48 > screenHeight)
        {
            y = 48;
        }

        return new WindowPlacement(
            string.IsNullOrWhiteSpace(saved.MonitorId) ? "primary" : saved.MonitorId,
            x,
            y,
            width,
            height,
            scale);
    }

    private static string GetWindowClass(nint hwnd)
    {
        var sb = new StringBuilder(256);
        int len = GetClassNameW(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : string.Empty;
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowExW(nint hWndParent, nint hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetParent(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();
}
