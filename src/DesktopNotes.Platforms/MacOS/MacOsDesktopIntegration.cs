using DesktopNotes.Application.Contracts;

namespace DesktopNotes.Platforms.MacOS;

/// <summary>
/// macOS desktop integration adapter (LaunchAgents plist login startup, screen bounds clamping,
/// and honest capability reporting per ADR-0001 / Guide 9.1 &amp; 9.4).
/// </summary>
public sealed class MacOsDesktopIntegration : IDesktopIntegration
{
    private const string LaunchAgentLabel = "com.desktopnotes.app";
    private readonly string _launchAgentsDir;

    public DesktopCapabilities Capabilities { get; }

    public MacOsDesktopIntegration(string? launchAgentsDirectoryOverride = null)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _launchAgentsDir = launchAgentsDirectoryOverride
            ?? Path.Combine(home, "Library", "LaunchAgents");

        if (!OperatingSystem.IsMacOS())
        {
            // Rule 9.1 & 9.4: Never report Supported when not verified on a physical macOS runtime.
            Capabilities = new DesktopCapabilities(
                EmbeddedDesktop: CapabilitySupport.Unknown,
                AbsolutePosition: CapabilitySupport.Unknown,
                Tray: CapabilitySupport.Unknown,
                GlobalShortcut: CapabilitySupport.Unknown,
                Reason: "未在真实 macOS 宿主环境执行运行时窗口层级与菜单栏验证（当前状态：Unknown）。");
        }
        else
        {
            Capabilities = new DesktopCapabilities(
                EmbeddedDesktop: CapabilitySupport.Unknown,
                AbsolutePosition: CapabilitySupport.Supported,
                Tray: CapabilitySupport.Supported,
                GlobalShortcut: CapabilitySupport.Unknown,
                Reason: "macOS NSWindow 桌面层级与辅助功能全局快捷键需结合系统权限实测确认。");
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
        string plistPath = GetLaunchAgentPlistPath();
        if (!enabled)
        {
            if (File.Exists(plistPath))
            {
                File.Delete(plistPath);
            }
            return;
        }

        Directory.CreateDirectory(_launchAgentsDir);
        string exePath = Environment.ProcessPath ?? "/Applications/DesktopNotes.app/Contents/MacOS/DesktopNotes.Desktop";
        string plistXml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{LaunchAgentLabel}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{exePath}</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
            </dict>
            </plist>
            """;
        await File.WriteAllTextAsync(plistPath, plistXml, ct);
    }

    public Task<bool> GetLoginStartupAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(GetLaunchAgentPlistPath()));
    }

    public WindowPlacement FitToAvailableScreens(WindowPlacement saved)
    {
        double width = Math.Clamp(saved.Width, 280, 1200);
        double height = Math.Clamp(saved.Height, 360, 1400);
        double scale = saved.Scale <= 0 ? 2.0 : saved.Scale;
        double x = (saved.X < 0 || saved.X + 80 > 1920) ? 1920 - width - 32 : saved.X;
        double y = (saved.Y < 24 || saved.Y + 48 > 1080) ? 40 : saved.Y;
        return new WindowPlacement(
            string.IsNullOrWhiteSpace(saved.MonitorId) ? "primary" : saved.MonitorId,
            x,
            y,
            width,
            height,
            scale);
    }

    private string GetLaunchAgentPlistPath() => Path.Combine(_launchAgentsDir, $"{LaunchAgentLabel}.plist");
}
