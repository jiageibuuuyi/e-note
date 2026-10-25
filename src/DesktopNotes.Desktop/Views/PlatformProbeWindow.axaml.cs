using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DesktopNotes.Platforms.Windows;
using PlacementMode = DesktopNotes.Application.Contracts.PlacementMode;

namespace DesktopNotes.Desktop.Views;

public partial class PlatformProbeWindow : Window
{
    private readonly DesktopHostProbe _probe = new();

    public PlatformProbeWindow()
    {
        InitializeComponent();

        if (DropTarget is not null)
        {
            DragDrop.SetAllowDrop(DropTarget, true);
            DropTarget.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            DropTarget.AddHandler(DragDrop.DropEvent, OnDrop);
        }

        if (BtnTopmost is not null)
        {
            BtnTopmost.Click += OnTopmostClick;
        }

        if (BtnEmbedded is not null)
        {
            BtnEmbedded.Click += OnEmbeddedClick;
        }

        if (BtnProbe is not null)
        {
            BtnProbe.Click += OnProbeClick;
        }

        Opened += (_, _) => RefreshProbeStatus("普通窗口基线");
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Move;
        var files = e.DataTransfer.TryGetFiles();
        if (files is not null)
        {
            var names = files
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToArray();

            if (names.Length > 0)
            {
                string summary = $"收到拖放条目 ({names.Length}): {string.Join(", ", names)} (仅记录名称，未移动)";
                if (DropTargetText is not null)
                {
                    DropTargetText.Text = summary;
                }
                RefreshProbeStatus(summary);
                e.Handled = true;
                return;
            }
        }

        RefreshProbeStatus("拖放未包含本地文件条目");
        e.Handled = true;
    }

    private async void OnTopmostClick(object? sender, RoutedEventArgs e)
    {
        await ApplyPlacementModeAsync(PlacementMode.Topmost);
    }

    private async void OnEmbeddedClick(object? sender, RoutedEventArgs e)
    {
        await ApplyPlacementModeAsync(PlacementMode.EmbeddedDesktop);
    }

    private void OnProbeClick(object? sender, RoutedEventArgs e)
    {
        RefreshProbeStatus("手动刷新探针");
    }

    private async Task ApplyPlacementModeAsync(PlacementMode mode)
    {
        try
        {
            nint hwnd = TryGetPlatformHandle()?.Handle ?? nint.Zero;
            Topmost = mode == PlacementMode.Topmost;
            await _probe.ApplyModeAsync(hwnd, mode, CancellationToken.None);
            RefreshProbeStatus($"已切换模式：{mode}");
        }
        catch (Exception ex)
        {
            RefreshProbeStatus($"模式切换失败 ({mode}): {ex.Message}");
        }
    }

    private void RefreshProbeStatus(string actionNote)
    {
        var discovery = _probe.ProbeDesktopHost();
        if (ProbeStatus is not null)
        {
            ProbeStatus.Text = $"{actionNote}\n{discovery.DiagnosticSummary}";
        }
    }
}
