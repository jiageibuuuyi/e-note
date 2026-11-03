using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DesktopNotes.Desktop.ViewModels;
using DesktopNotes.Domain;

namespace DesktopNotes.Desktop.Controls;

public partial class TaskRow : UserControl
{
    private static Guid? s_activeDraggedTaskId;
    public static Guid? ActiveDraggedTaskId => s_activeDraggedTaskId;
    private TaskRowViewModel? _subscribedVm;
    private Point? _pointerPressPoint;
    private PointerPressedEventArgs? _lastPressEventArgs;
    private bool _pointerPressedOnHandle;
    private bool _pointerPressedInTextBox;
    private bool _isDraggingTask;

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
            RowContainer.AddHandler(
                InputElement.PointerMovedEvent,
                OnRowPointerMoved,
                RoutingStrategies.Tunnel);
            RowContainer.AddHandler(
                InputElement.PointerReleasedEvent,
                OnRowPointerReleased,
                RoutingStrategies.Tunnel);
            RowContainer.AddHandler(DragDrop.DragOverEvent, OnRowDragOver);
            RowContainer.AddHandler(DragDrop.DragLeaveEvent, OnRowDragLeave);
            RowContainer.AddHandler(DragDrop.DropEvent, OnRowDrop);
        }

        if (TreeToggleButton is not null)
        {
            TreeToggleButton.Click += OnTreeToggleButtonClick;
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

        if (RealisticPlantButton is not null)
        {
            RealisticPlantButton.Click += OnCompleteCheckBoxClick;
        }

        if (MenuEditRow is not null)
        {
            MenuEditRow.Click += OnEditRowMenuClick;
        }

        if (MenuCopyText is not null)
        {
            MenuCopyText.Click += OnCopyTextMenuClick;
        }

        if (MenuPasteText is not null)
        {
            MenuPasteText.Click += OnPasteTextMenuClick;
        }

        if (RowContainer is not null)
        {
            RowContainer.AddHandler(InputElement.PointerReleasedEvent, (s, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Right)
                {
                    if (DataContext is TaskRowViewModel { IsEditingInline: true })
                    {
                        return;
                    }

                    if (RowContainer.ContextMenu is { } cm)
                    {
                        cm.DataContext = DataContext;
                        try
                        {
                            if (!cm.IsOpen)
                            {
                                cm.Open(RowContainer);
                            }
                        }
                        catch
                        {
                        }
                        e.Handled = true;
                    }
                }
            }, RoutingStrategies.Tunnel);
        }



        if (PencilEditButton is not null)
        {
            PencilEditButton.Click += OnOpenDetailButtonClick;
        }

        if (BtnRealisticDelete is not null)
        {
            BtnRealisticDelete.Click += OnTrashRowClick;
        }

        if (BtnMinimalistDelete is not null)
        {
            BtnMinimalistDelete.Click += OnTrashRowClick;
        }

        if (BtnDrawerDelete is not null)
        {
            BtnDrawerDelete.Click += OnTrashRowClick;
        }

        if (BtnQuickProgress is not null)
        {
            BtnQuickProgress.Click += OnQuickProgressClick;
        }

        if (PromoteSubtaskButton is not null)
        {
            PromoteSubtaskButton.Click += OnPromoteSubtaskClick;
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

        if (MenuPromoteSubtask is not null)
        {
            MenuPromoteSubtask.Click += OnPromoteSubtaskClick;
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
        _pointerPressPoint = null;
        _lastPressEventArgs = null;
        _pointerPressedOnHandle = false;
        _pointerPressedInTextBox = false;

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

            if (DragHandleBorder is not null &&
                e.Source is Visual handleSrc &&
                (handleSrc == DragHandleBorder || DragHandleBorder.IsVisualAncestorOf(handleSrc)))
            {
                if (vm.Record.Kind == RecordKind.Task)
                {
                    _pointerPressPoint = e.GetPosition(this);
                    _lastPressEventArgs = e;
                    _pointerPressedOnHandle = true;
                    e.Handled = true;
                }
                return;
            }

            bool isInsideTextControl = false;
            if (e.Source is Visual src)
            {
                isInsideTextControl =
                    src.FindAncestorOfType<TextBox>(includeSelf: true) is not null ||
                    src.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) is not null;
            }

            if (vm.Record.Kind == RecordKind.Task && e.ClickCount == 1)
            {
                _pointerPressPoint = e.GetPosition(this);
                _lastPressEventArgs = e;
                _pointerPressedInTextBox = isInsideTextControl;
            }

            if (isInsideTextControl)
            {
                if (e.ClickCount >= 2)
                {
                    RowClicked?.Invoke(vm.Id, e.ClickCount);
                    if (FindParentPanelViewModel() is { } pVm)
                    {
                        pVm.RowInteraction.HandleRowClick(vm.Id, e.ClickCount);
                    }
                    e.Handled = true;
                    return;
                }

                // When single-clicking inside the editable/selectable title or body:
                // Allow native caret placement and text selection without prematurely expanding drawer
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

    private async void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDraggingTask || _pointerPressPoint is not { } startPoint)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pointerPressPoint = null;
            return;
        }

        if (DataContext is not TaskRowViewModel { IsEditingInline: false } vm ||
            vm.Record.Kind != RecordKind.Task)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        double dx = Math.Abs(currentPoint.X - startPoint.X);
        double dy = Math.Abs(currentPoint.Y - startPoint.Y);

        bool shouldStartDrag = false;
        if (_pointerPressedOnHandle)
        {
            shouldStartDrag = dx >= 2 || dy >= 2;
        }
        else if (_pointerPressedInTextBox)
        {
            // Inside TitleDisplayBox: horizontal movement selects text for copying;
            // deliberate vertical drag (up/down across rows) initiates task-to-subtask drag!
            shouldStartDrag = dy >= 12 && dy > dx * 1.6;
        }
        else
        {
            shouldStartDrag = dx >= 5 || dy >= 5;
        }

        if (!shouldStartDrag || _lastPressEventArgs is not { } pressArgs)
        {
            return;
        }

        _pointerPressPoint = null;
        _lastPressEventArgs = null;
        _isDraggingTask = true;
        s_activeDraggedTaskId = vm.Id;

        try
        {
            e.Pointer.Capture(null);
            if (RowContainer is not null)
            {
                RowContainer.Opacity = 0.65;
            }

            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(vm.Id.ToString()));
            await DragDrop.DoDragDropAsync(pressArgs, transfer, DragDropEffects.Move | DragDropEffects.Link);
        }
        catch
        {
            // Ignore drag cancellation
        }
        finally
        {
            if (RowContainer is not null)
            {
                RowContainer.Opacity = 1.0;
            }
            s_activeDraggedTaskId = null;
            _isDraggingTask = false;
        }
    }

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pointerPressPoint = null;
        _pointerPressedOnHandle = false;
        _pointerPressedInTextBox = false;
    }

    private async void OnTreeToggleButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TaskRowViewModel { IsEditingInline: false, HasSubtasks: true } vm &&
            FindParentPanelViewModel() is { } panelVm)
        {
            e.Handled = true;
            await panelVm.ToggleTreeCollapseAsync(vm.Id, CancellationToken.None);
        }
    }

    private void OnEditRowMenuClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TaskRowViewModel vm)
        {
            if (vm.IsMarkedCompleted)
            {
                OnOpenDetailButtonClick(sender, e);
                return;
            }

            if (TitleDisplayBox is not null)
            {
                TitleDisplayBox.Focus();
                TitleDisplayBox.SelectAll();
            }
        }
    }

    private void OnCopyTextMenuClick(object? sender, RoutedEventArgs e)
    {
        if (TitleDisplayBox is not null)
        {
            if (string.IsNullOrEmpty(TitleDisplayBox.SelectedText))
            {
                TitleDisplayBox.SelectAll();
            }
            TitleDisplayBox.Copy();
        }
    }

    private void OnPasteTextMenuClick(object? sender, RoutedEventArgs e)
    {
        if (TitleDisplayBox is not null)
        {
            TitleDisplayBox.Paste();
        }
    }

    private void OnQuickProgressClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is TaskRowViewModel vm &&
            FindParentPanelViewModel() is { } panelVm)
        {
            panelVm.CycleTaskProgress(vm.Id);
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

    private async void OnPromoteSubtaskClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TaskRowViewModel { IsEditingInline: false, IsSubtask: true } vm &&
            FindParentPanelViewModel() is { } panelVm)
        {
            e.Handled = true;
            await panelVm.PromoteSubtaskToRootAsync(vm.Id, CancellationToken.None);
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

    private void SetDropTargetHighlight(bool highlighted)
    {
        if (RowContainer is null)
        {
            return;
        }

        if (highlighted)
        {
            RowContainer.BorderBrush = new SolidColorBrush(Color.Parse("#0078D4"));
            RowContainer.BorderThickness = new Thickness(1.5);
        }
        else
        {
            RowContainer.ClearValue(Border.BorderBrushProperty);
            RowContainer.ClearValue(Border.BorderThicknessProperty);
        }
    }

    private void OnRowDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not TaskRowViewModel { IsEditingInline: false } vm)
        {
            return;
        }

        if (s_activeDraggedTaskId is { } draggedId)
        {
            if (vm.Record.Kind == RecordKind.Task && draggedId != vm.Id)
            {
                e.DragEffects = DragDropEffects.Move;
                SetDropTargetHighlight(true);
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
                SetDropTargetHighlight(false);
            }
            e.Handled = true;
            return;
        }

        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            SetDropTargetHighlight(true);
            e.Handled = true;
        }
    }

    private void OnRowDragLeave(object? sender, DragEventArgs e)
    {
        SetDropTargetHighlight(false);
    }

    private async void OnRowDrop(object? sender, DragEventArgs e)
    {
        SetDropTargetHighlight(false);

        if (DataContext is not TaskRowViewModel { IsEditingInline: false } vm ||
            FindParentPanelViewModel() is not { } panelVm)
        {
            return;
        }

        if (s_activeDraggedTaskId is { } draggedTaskId)
        {
            s_activeDraggedTaskId = null;
            if (draggedTaskId != vm.Id && vm.Record.Kind == RecordKind.Task)
            {
                e.Handled = true;
                await panelVm.ConvertTaskToSubtaskAsync(draggedTaskId, vm.Id, CancellationToken.None);
            }
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
