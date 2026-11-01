using System.ComponentModel;
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
    private TaskRowViewModel? _subscribedVm;
    public event Action<Guid, int>? RowClicked;
    public event Action<Guid>? CompleteRequested;
    public event Action<Guid>? OpenDetailClicked;

    public TaskRow()
    {
        InitializeComponent();

        if (RowContainer is not null)
        {
            RowContainer.AddHandler(
                InputElement.PointerPressedEvent,
                OnRowPointerPressed,
                RoutingStrategies.Tunnel);
            RowContainer.AddHandler(DragDrop.DragOverEvent, OnRowDragOver);
            RowContainer.AddHandler(DragDrop.DropEvent, OnRowDrop);
        }

        if (TitleDisplayBox is not null)
        {
            TitleDisplayBox.KeyDown += OnTitleDisplayBoxKeyDown;
            TitleDisplayBox.LostFocus += OnTitleDisplayBoxLostFocus;
        }

        if (CompleteCheckBox is not null)
        {
            CompleteCheckBox.Click += OnCompleteCheckBoxClick;
        }

        if (OpenDetailButton is not null)
        {
            OpenDetailButton.Click += OnOpenDetailButtonClick;
        }

        if (AddSubtaskButton is not null)
        {
            AddSubtaskButton.Click += OnAddSubtaskButtonClick;
        }

        if (MenuAddSubtask is not null)
        {
            MenuAddSubtask.Click += OnAddSubtaskButtonClick;
        }

        if (MenuOpenDetail is not null)
        {
            MenuOpenDetail.Click += OnOpenDetailButtonClick;
        }

        if (MenuCompleteRow is not null)
        {
            MenuCompleteRow.Click += OnCompleteCheckBoxClick;
        }

        if (ConfirmArchiveButton is not null)
        {
            ConfirmArchiveButton.Click += OnConfirmArchiveClick;
        }

        if (MenuArchiveRow is not null)
        {
            MenuArchiveRow.Click += OnConfirmArchiveClick;
        }

        if (MenuTrashRow is not null)
        {
            MenuTrashRow.Click += OnTrashRowClick;
        }

        if (InlineTitleBox is not null)
        {
            InlineTitleBox.KeyDown += OnInlineTitleBoxKeyDown;
        }

        if (InlineSaveButton is not null)
        {
            InlineSaveButton.Click += OnInlineSaveButtonClick;
        }

        if (InlineCancelButton is not null)
        {
            InlineCancelButton.Click += OnInlineCancelButtonClick;
        }

        DataContextChanged += OnTaskRowDataContextChanged;
        AttachedToVisualTree += OnTaskRowAttachedToVisualTree;
    }

    private void OnTaskRowDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedVm = null;
        }

        if (DataContext is TaskRowViewModel vm)
        {
            _subscribedVm = vm;
            _subscribedVm.PropertyChanged += OnViewModelPropertyChanged;
            if (vm.IsExpanded)
            {
                EnsureExpandedRowVisible();
            }
        }

        TryFocusInlineBox();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskRowViewModel.IsExpanded) &&
            DataContext is TaskRowViewModel { IsExpanded: true })
        {
            EnsureExpandedRowVisible();
        }
    }

    private void EnsureExpandedRowVisible()
    {
        Dispatcher.UIThread.Post(() =>
        {
            this.BringIntoView();
            ExpandedDrawerBorder?.BringIntoView();
        }, DispatcherPriority.Loaded);

        Dispatcher.UIThread.Post(() =>
        {
            this.BringIntoView();
            ExpandedDrawerBorder?.BringIntoView();
        }, DispatcherPriority.Background);
    }

    private async void OnTitleDisplayBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not TaskRowViewModel { IsEditingInline: false } vm)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (FindParentPanelViewModel() is { } panelVm)
            {
                await panelVm.SaveEditedTitleAsync(vm, CancellationToken.None);
            }
            Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.EditableTitle = !string.IsNullOrWhiteSpace(vm.Record.Title) ? vm.Record.Title : vm.Record.DisplayTitle;
            Focus();
        }
    }

    private async void OnTitleDisplayBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TaskRowViewModel { IsEditingInline: false, IsTitleDirty: true } vm &&
            FindParentPanelViewModel() is { } panelVm)
        {
            await panelVm.SaveEditedTitleAsync(vm, CancellationToken.None);
        }
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

    private async void OnInlineSaveButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TaskRowViewModel { IsEditingInline: true } vm &&
            FindParentPanelViewModel() is { } panelVm)
        {
            e.Handled = true;
            await panelVm.CommitInlineCreateAsync(vm, CancellationToken.None);
        }
    }

    private void OnInlineCancelButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TaskRowViewModel { IsEditingInline: true } vm &&
            FindParentPanelViewModel() is { } panelVm)
        {
            e.Handled = true;
            panelVm.CancelInlineCreate(vm);
        }
    }

    private PanelViewModel? FindParentPanelViewModel()
    {
        var window = this.FindAncestorOfType<Window>();
        return window?.DataContext as PanelViewModel;
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Visual sourceVisual)
        {
            // Let buttons and checkboxes handle their own clicks without triggering row expand/collapse
            if (sourceVisual.FindAncestorOfType<Button>(includeSelf: true) is not null ||
                sourceVisual.FindAncestorOfType<CheckBox>(includeSelf: true) is not null)
            {
                return;
            }
        }

        if (DataContext is TaskRowViewModel vm)
        {
            if (vm.IsEditingInline)
            {
                InlineTitleBox?.Focus();
                return;
            }

            bool isInsideTextControl = false;
            if (e.Source is Visual src)
            {
                isInsideTextControl =
                    src.FindAncestorOfType<TextBox>(includeSelf: true) is not null ||
                    src.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) is not null;
            }

            if (isInsideTextControl)
            {
                // When clicking inside the editable/selectable title or body:
                // 1) Expand this row if it is not yet expanded so the user immediately sees details
                // 2) Do NOT mark e.Handled = true so mouse drag selection (Ctrl+C copy) and text editing work natively!
                if (!vm.IsExpanded && e.ClickCount == 1 && FindParentPanelViewModel() is { } pvm)
                {
                    pvm.ExpandedRecordId = vm.Id;
                }
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

    private void OnAddSubtaskButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TaskRowViewModel { IsEditingInline: false, CanAddSubtask: true } vm &&
            FindParentPanelViewModel() is { } panelVm)
        {
            panelVm.StartInlineSubtaskCreate(vm.Id);
            e.Handled = true;
        }
    }

    private async void OnTrashRowClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TaskRowViewModel { IsEditingInline: false } vm &&
            FindParentPanelViewModel() is { } panelVm)
        {
            await panelVm.TrashRecordAsync(vm.Id, CancellationToken.None);
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
                panelVm.ToggleCompletedMark(vm.Id);
            }
            e.Handled = true;
        }
    }

    private async void OnConfirmArchiveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TaskRowViewModel { IsEditingInline: false } vm &&
            FindParentPanelViewModel() is { } panelVm)
        {
            e.Handled = true;
            await panelVm.ConfirmArchiveRecordAsync(vm.Id, CancellationToken.None);
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
