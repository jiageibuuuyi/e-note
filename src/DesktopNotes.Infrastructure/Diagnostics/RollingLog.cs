using System.Text;

namespace DesktopNotes.Infrastructure.Diagnostics;

/// <summary>
/// Size-bounded rolling diagnostic logger that strictly records operation codes, record IDs, and sanitized paths
/// without ever writing user task/note body content.
/// </summary>
public sealed class RollingLog
{
    private readonly string _logDirectory;
    private readonly long _maxFileBytes;
    private readonly int _maxRetainedFiles;
    private readonly object _sync = new();

    public RollingLog(string logDirectory, long maxFileBytes = 5 * 1024 * 1024, int maxRetainedFiles = 5)
    {
        _logDirectory = Path.GetFullPath(logDirectory);
        _maxFileBytes = Math.Max(4096, maxFileBytes);
        _maxRetainedFiles = Math.Max(2, maxRetainedFiles);
        Directory.CreateDirectory(_logDirectory);
    }

    public void WriteEvent(string level, string category, string code, Guid? entityId = null, string? diagnosticDetail = null)
    {
        string timestamp = DateTimeOffset.UtcNow.ToString("O");
        string idPart = entityId.HasValue ? $" entity={entityId.Value:D}" : string.Empty;
        string detailPart = string.IsNullOrWhiteSpace(diagnosticDetail)
            ? string.Empty
            : $" detail={SanitizeSingleLine(diagnosticDetail)}";
        string line = $"{timestamp} [{level}] [{category}] code={code}{idPart}{detailPart}{Environment.NewLine}";

        lock (_sync)
        {
            string activeLogPath = Path.Combine(_logDirectory, "desktopnotes.log");
            RotateIfNeeded(activeLogPath, Encoding.UTF8.GetByteCount(line));
            File.AppendAllText(activeLogPath, line, Encoding.UTF8);
        }
    }

    private void RotateIfNeeded(string activeLogPath, int incomingBytes)
    {
        if (!File.Exists(activeLogPath))
        {
            return;
        }

        var info = new FileInfo(activeLogPath);
        if (info.Length + incomingBytes <= _maxFileBytes)
        {
            return;
        }

        // Delete oldest rotated file if at capacity
        for (int i = _maxRetainedFiles - 1; i >= 1; i--)
        {
            string older = Path.Combine(_logDirectory, $"desktopnotes.{i}.log");
            string newer = i == 1 ? activeLogPath : Path.Combine(_logDirectory, $"desktopnotes.{i - 1}.log");

            if (i == _maxRetainedFiles - 1 && File.Exists(older))
            {
                File.Delete(older);
            }

            if (File.Exists(newer))
            {
                File.Move(newer, older, overwrite: true);
            }
        }
    }

    private static string SanitizeSingleLine(string raw)
    {
        return raw.Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
