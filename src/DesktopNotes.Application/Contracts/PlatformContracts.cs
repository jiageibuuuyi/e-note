namespace DesktopNotes.Application.Contracts;

public enum PlacementMode
{
    Topmost = 0,
    EmbeddedDesktop = 1,
    Normal = 2
}

public enum VisibilityMode
{
    Shown = 0,
    TrayHidden = 1
}

public enum CapabilitySupport
{
    Unknown = 0,
    Supported = 1,
    Unsupported = 2
}

public sealed record DesktopCapabilities(
    CapabilitySupport EmbeddedDesktop,
    CapabilitySupport AbsolutePosition,
    CapabilitySupport Tray,
    CapabilitySupport GlobalShortcut,
    string Reason);

public sealed record WindowPlacement(
    string MonitorId,
    double X,
    double Y,
    double Width,
    double Height,
    double Scale);

public interface IDesktopIntegration
{
    DesktopCapabilities Capabilities { get; }
    Task ApplyModeAsync(nint windowHandle, PlacementMode mode, CancellationToken ct);
    Task SetLoginStartupAsync(bool enabled, CancellationToken ct);
    Task<bool> GetLoginStartupAsync(CancellationToken ct);
    WindowPlacement FitToAvailableScreens(WindowPlacement saved);
}
