using DesktopNotes.Application.Contracts;

namespace DesktopNotes.Platforms.Linux;

/// <summary>
/// Linux desktop integration adapter supporting XDG autostart (.desktop), X11 vs Wayland capability reporting,
/// and safe fallback behavior on Wayland compositors where absolute positioning / desktop embedding are restricted.
/// </summary>
public sealed class LinuxDesktopIntegration : IDesktopIntegration
{
    private readonly string _autostartDir;
    private readonly string? _sessionType;

    public DesktopCapabilities Capabilities { get; }

    public LinuxDesktopIntegration(string? autostartDirectoryOverride = null, string? sessionTypeOverride = null)
    {
        string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        _autostartDir = autostartDirectoryOverride ?? Path.Combine(configHome, "autostart");
        _sessionType = sessionTypeOverride ?? Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");

        bool isWayland = string.Equals(_sessionType, "wayland", StringComparison.OrdinalIgnoreCase);

        if (!OperatingSystem.IsLinux() && sessionTypeOverride is null)
        {
            Capabilities = new DesktopCapabilities(
                EmbeddedDesktop: CapabilitySupport.Unknown,
                AbsolutePosition: CapabilitySupport.Unknown,
                Tray: CapabilitySupport.Unknown,
                GlobalShortcut: CapabilitySupport.Unknown,
                Reason: "未在真实 Linux 宿主环境运行验证（当前状态：Unknown）。");
        }
        else if (isWayland)
        {
            // Rule 9.4 & T12: On Wayland compositors, top-level absolute window coordinates and desktop embedding
            // are restricted by protocol; report Unsupported with explicit fallback explanation rather than claiming equivalence.
            Capabilities = new DesktopCapabilities(
                EmbeddedDesktop: CapabilitySupport.Unsupported,
                AbsolutePosition: CapabilitySupport.Unsupported,
                Tray: CapabilitySupport.Unknown,
                GlobalShortcut: CapabilitySupport.Unsupported,
                Reason: "Wayland 会话下合成器限制客户端绝对坐标定位与桌面底层嵌入，回退为由合成器管理的置顶/常规面板模式。");
        }
        else
        {
            Capabilities = new DesktopCapabilities(
                EmbeddedDesktop: CapabilitySupport.Unknown,
                AbsolutePosition: CapabilitySupport.Supported,
                Tray: CapabilitySupport.Unknown,
                GlobalShortcut: CapabilitySupport.Unknown,
                Reason: "X11 会话支持窗口坐标定位；桌面底层嵌入与 StatusNotifierItem 托盘需视具体桌面环境 (GNOME/KDE) 验证。");
        }
    }

    public Task ApplyModeAsync(nint windowHandle, PlacementMode mode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task SetLoginStartupAsync(bool enabled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string desktopFilePath = GetAutostartFilePath();
        if (!enabled)
        {
            if (File.Exists(desktopFilePath))
            {
                File.Delete(desktopFilePath);
            }
            return;
        }

        Directory.CreateDirectory(_autostartDir);
        string exePath = Environment.ProcessPath ?? "/usr/bin/desktopnotes";
        string content = $"""
            [Desktop Entry]
            Type=Application
            Name=DesktopNotes
            Comment=Lightweight Desktop Todo and Notes
            Exec="{exePath}"
            Terminal=false
            X-GNOME-Autostart-enabled=true
            """;
        await File.WriteAllTextAsync(desktopFilePath, content, ct);
    }

    public Task<bool> GetLoginStartupAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(GetAutostartFilePath()));
    }

    public WindowPlacement FitToAvailableScreens(WindowPlacement saved)
    {
        double width = Math.Clamp(saved.Width, 280, 1200);
        double height = Math.Clamp(saved.Height, 360, 1400);
        double scale = saved.Scale <= 0 ? 1.0 : saved.Scale;
        double x = (saved.X < 0 || saved.X + 80 > 1920) ? 1920 - width - 32 : saved.X;
        double y = (saved.Y < 0 || saved.Y + 48 > 1080) ? 48 : saved.Y;
        return new WindowPlacement(
            string.IsNullOrWhiteSpace(saved.MonitorId) ? "primary" : saved.MonitorId,
            x,
            y,
            width,
            height,
            scale);
    }

    private string GetAutostartFilePath() => Path.Combine(_autostartDir, "desktopnotes.desktop");
}
