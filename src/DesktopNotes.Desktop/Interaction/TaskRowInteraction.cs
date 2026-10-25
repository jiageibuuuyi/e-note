using System.Runtime.InteropServices;

namespace DesktopNotes.Desktop.Interaction;

public sealed class TaskRowInteraction
{
    private readonly Func<Guid?> _getExpandedId;
    private readonly Action<Guid?> _setExpandedId;
    private readonly Action<Guid> _openDetail;
    private readonly TimeSpan _doubleClickWindow;
    private CancellationTokenSource? _pendingCollapseCts;
    private Guid? _pendingCollapseTarget;

    public TaskRowInteraction(
        Func<Guid?> getExpandedId,
        Action<Guid?> setExpandedId,
        Action<Guid> openDetail,
        TimeSpan? doubleClickThresholdOverride = null)
    {
        _getExpandedId = getExpandedId ?? throw new ArgumentNullException(nameof(getExpandedId));
        _setExpandedId = setExpandedId ?? throw new ArgumentNullException(nameof(setExpandedId));
        _openDetail = openDetail ?? throw new ArgumentNullException(nameof(openDetail));
        _doubleClickWindow = doubleClickThresholdOverride ?? GetSystemDoubleClickTime();
    }

    public void HandleRowClick(Guid recordId, int clickCount)
    {
        if (clickCount <= 0)
        {
            return;
        }

        if (clickCount >= 2)
        {
            // Cancel any pending collapse so double-clicking an already-expanded row
            // never collapses it before opening the detail window.
            CancelPendingCollapse();
            if (_getExpandedId() != recordId)
            {
                _setExpandedId(recordId);
            }
            _openDetail(recordId);
            return;
        }

        // clickCount == 1
        var currentExpanded = _getExpandedId();
        if (currentExpanded != recordId)
        {
            CancelPendingCollapse();
            _setExpandedId(recordId);
            return;
        }

        // Clicking an already-expanded row: delay collapse until system double-click window expires
        CancelPendingCollapse();
        var cts = new CancellationTokenSource();
        _pendingCollapseCts = cts;
        _pendingCollapseTarget = recordId;

        _ = CollapseAfterDelayAsync(recordId, _doubleClickWindow, cts.Token);
    }

    public void CancelCandidateClick()
    {
        CancelPendingCollapse();
    }

    private async Task CollapseAfterDelayAsync(Guid targetId, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            if (!ct.IsCancellationRequested && _getExpandedId() == targetId && _pendingCollapseTarget == targetId)
            {
                _setExpandedId(null);
            }
        }
        catch (OperationCanceledException)
        {
            // Double-click or drag cancelled collapse
        }
    }

    private void CancelPendingCollapse()
    {
        if (_pendingCollapseCts is not null)
        {
            _pendingCollapseCts.Cancel();
            _pendingCollapseCts.Dispose();
            _pendingCollapseCts = null;
        }
        _pendingCollapseTarget = null;
    }

    public static TimeSpan GetSystemDoubleClickTime()
    {
        if (OperatingSystem.IsWindows())
        {
            uint ms = GetDoubleClickTime();
            if (ms is >= 100 and <= 2000)
            {
                return TimeSpan.FromMilliseconds(ms);
            }
        }

        return TimeSpan.FromMilliseconds(300);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
}
