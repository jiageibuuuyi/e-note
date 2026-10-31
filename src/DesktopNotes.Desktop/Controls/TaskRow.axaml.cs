using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DesktopNotes.Desktop.ViewModels;

namespace DesktopNotes.Desktop.Controls;

public partial class TaskRow : UserControl
{
    public event Action<Guid, int>? RowClicked;
    public event Action<Guid>? CompleteRequested;
    public event Action<Guid>? OpenDetailClicked;

    public TaskRow()
    {
        InitializeComponent();

        if (RowContainer is not null)
        {
            RowContainer.PointerPressed += OnRowPointerPressed;
            RowContainer.AddHandler(DragDrop.DragOverEvent, OnRowDragOver);
            RowContainer.AddHandler(DragDrop.DropEvent, OnRowDrop);
        }

        if (CompleteCheckBox is not null)
        {
            CompleteCheckBox.Click += OnCompleteCheckBoxClick;
        }

        if (OpenDetailButton is not null)
        {
            OpenDetailButton.Click += OnOpenDetailButtonClick;
        }

        if (InlineTitleBox is not null)
        {
            InlineTitleBox.KeyDown += OnInlineTitleBoxKeyDown;
            InlineTitleBox.LostFocus += OnInlineTitleBoxLostFocus;
        }

        DataContextChanged += OnTaskRowDataContextChanged;
        AttachedToVisualTree += OnTaskRowAttachedToVisualTree;
    }

    private void OnTaskRowDataContextChanged(object? sender, EventArgs e)
    {
        TryFocusInlineBox();
    }

    private void OnTaskRowAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        TryFocusInlineBox();
    }

    private void TryFocusInlineBox()
    {
        if (DataContext is TaskRowViewModel { IsEditingInline: true } && InlineTitleBox is not null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                InlineTitleBox.Focus();
                InlineTitleBox.CaretIndex = InlineTitleBox.Text?.Length ?? 0;
            }, DispatcherPriority.Input);
        }
    }

    private async void OnInlineTitleBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not TaskRowViewModel { IsEditingInline: true } vm)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (FindParentPanelViewModel() is { } panelVm)
            {
                await panelVm.CommitInlineCreateAsync(vm, CancellationToken.None);
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (FindParentPanelViewModel() is { } panelVm)
            {
                panelVm.CancelInlineCreate(vm);
            }
        }
    }

    private async void OnInlineTitleBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TaskRowViewModel { IsEditingInline: true } vm &&
            FindParentPanelViewModel() is { } panelVm)
        {
            await panelVm.CommitInlineCreateAsync(vm, CancellationToken.None);
        }
    }

    private PanelViewModel? FindParentPanelViewModel()
    {
        var window = this.FindAncestorOfType<Window>();
        return window?.DataContext as PanelViewModel;
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is TaskRowViewModel vm)
        {
            if (vm.IsEditingInline)
            {
                return;
            }

            RowClicked?.Invoke(vm.Id, e.ClickCount);
            if (FindParentPanelViewModel() is { } panelVm)
            {
                panelVm.RowInteraction.HandleRowClick(vm.Id, e.ClickCount);
            }
            e.Handled = true;
        }
    }

    private void OnCompleteCheckBoxClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TaskRowViewModel { IsEditingInline: false } vm)
        {
            CompleteRequested?.Invoke(vm.Id);
            if (FindParentPanelViewModel() is { } panelVm)
            {
                _ = panelVm.CompleteRecordAsync(vm.Id, includeChildren: false, CancellationToken.None);
            }
            e.Handled = true;
        }
    }

    private void OnOpenDetailButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TaskRowViewModel { IsEditingInline: false } vm)
        {
            OpenDetailClicked?.Invoke(vm.Id);
            if (FindParentPanelViewModel() is { } panelVm)
            {
                panelVm.RowInteraction.HandleRowClick(vm.Id, 2);
            }
            e.Handled = true;
        }
    }

    private void OnRowDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is TaskRowViewModel { IsEditingInline: false } &&
            e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Move | DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void OnRowDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not TaskRowViewModel { IsEditingInline: false } vm ||
            FindParentPanelViewModel() is not { } panelVm)
        {
            return;
        }

        var items = e.DataTransfer.TryGetFiles();
        if (items is null)
        {
            return;
        }

        var localPaths = items
            .Select(i => i.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>()
            .ToArray();

        if (localPaths.Length > 0)
        {
            e.Handled = true;
            await panelVm.DropFilesOnRecordAsync(vm.Id, localPaths, CancellationToken.None);
        }
    }
}
