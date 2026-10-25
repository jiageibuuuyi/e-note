using DesktopNotes.Application.Contracts;
using DesktopNotes.Platforms.Windows;
using Xunit;

namespace DesktopNotes.Integration.Tests;

public class PlatformProbeTests
{
    [Fact]
    public void DesktopHostProbe_reports_capabilities_and_discovers_windows_host()
    {
        var probe = new DesktopHostProbe();
        var caps = probe.Capabilities;

        if (OperatingSystem.IsWindows())
        {
            var discovery = probe.ProbeDesktopHost();
            Assert.True(discovery.Found, discovery.DiagnosticSummary);
            Assert.NotEqual(nint.Zero, discovery.ProgmanHandle);
            Assert.NotEqual(nint.Zero, discovery.DefViewParentHandle);
            Assert.Equal(CapabilitySupport.Supported, caps.EmbeddedDesktop);
            Assert.Equal(CapabilitySupport.Supported, caps.Tray);
        }
        else
        {
            Assert.Equal(CapabilitySupport.Unknown, caps.EmbeddedDesktop);
        }
    }

    [Fact]
    public void FitToAvailableScreens_recovers_offscreen_window_coordinates()
    {
        var probe = new DesktopHostProbe();
        var offscreen = new WindowPlacement("disconnected-monitor", -5000, -3000, 350, 520, 1.25);
        var fitted = probe.FitToAvailableScreens(offscreen);

        Assert.True(fitted.X >= 0);
        Assert.True(fitted.Y >= 0);
        Assert.Equal(350, fitted.Width);
        Assert.Equal(520, fitted.Height);
    }
}
