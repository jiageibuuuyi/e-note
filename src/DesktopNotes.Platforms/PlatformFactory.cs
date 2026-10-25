using DesktopNotes.Application.Contracts;
using DesktopNotes.Platforms.Linux;
using DesktopNotes.Platforms.MacOS;
using DesktopNotes.Platforms.Windows;

namespace DesktopNotes.Platforms;

public static class PlatformFactory
{
    public static IDesktopIntegration CreateDesktopIntegration()
    {
        if (OperatingSystem.IsWindows())
        {
            return new DesktopHostProbe();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsDesktopIntegration();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxDesktopIntegration();
        }

        return new DesktopHostProbe();
    }
}
