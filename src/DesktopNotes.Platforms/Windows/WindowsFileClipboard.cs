using DesktopNotes.Application.Contracts;

namespace DesktopNotes.Platforms.Windows;

public sealed class WindowsFileClipboard : IFileClipboard
{
    private readonly object _sync = new();
    private IReadOnlyList<string>? _files;
    private ImportIntent? _explicitDropEffectIntent;
    private string _currentToken = Guid.NewGuid().ToString("N");
    private readonly List<string> _acknowledgedTokens = new();

    public IReadOnlyList<string> AcknowledgedMoveTokens
    {
        get
        {
            lock (_sync)
            {
                return _acknowledgedTokens.ToArray();
            }
        }
    }

    public string CurrentToken
    {
        get
        {
            lock (_sync)
            {
                return _currentToken;
            }
        }
    }

    public string SetClipboardFiles(IReadOnlyList<string> filePaths, ImportIntent? preferredDropEffectIntent)
    {
        lock (_sync)
        {
            _files = filePaths.ToArray();
            _explicitDropEffectIntent = preferredDropEffectIntent;
            _currentToken = Guid.NewGuid().ToString("N");
            return _currentToken;
        }
    }

    public string SetClipboardText(string text)
    {
        lock (_sync)
        {
            // User copied plain text (including a raw file path string): plain text paths are never treated as file entries!
            _files = null;
            _explicitDropEffectIntent = null;
            _currentToken = $"text:{Guid.NewGuid():N}:{text}";
            return _currentToken;
        }
    }

    public Task<ClipboardFiles?> ReadFilesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_files is null || _files.Count == 0)
            {
                return Task.FromResult<ClipboardFiles?>(null);
            }

            // When clipboard lacks a reliable cut/move marker, default to Copy! Never guess Move.
            var resolvedIntent = _explicitDropEffectIntent == ImportIntent.Move
                ? ImportIntent.Move
                : ImportIntent.Copy;

            return Task.FromResult<ClipboardFiles?>(new ClipboardFiles(
                _files.ToArray(),
                resolvedIntent,
                _currentToken));
        }
    }

    public Task AcknowledgeMoveAsync(string clipboardToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            // Only update/clear clipboard move state if the clipboard still matches the token from the start of this import!
            // If the user copied new text or files during the transfer, preserve the new clipboard content.
            if (string.Equals(_currentToken, clipboardToken, StringComparison.Ordinal))
            {
                _acknowledgedTokens.Add(clipboardToken);
                _files = null;
                _explicitDropEffectIntent = null;
                _currentToken = Guid.NewGuid().ToString("N");
            }
        }

        return Task.CompletedTask;
    }
}
