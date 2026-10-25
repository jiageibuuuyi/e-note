namespace DesktopNotes.Platforms.Windows;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    public bool IsFirstInstance { get; }

    public SingleInstanceGuard(string instanceName = "Local\\DesktopNotes_SingleInstance_V1")
    {
        _mutex = new Mutex(initiallyOwned: true, instanceName, out bool createdNew);
        IsFirstInstance = createdNew;
    }

    public void Dispose()
    {
        if (IsFirstInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }
        _mutex.Dispose();
    }
}
