using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        }

        if (CompleteCheckBox is not null)
        {
            CompleteCheckBox.Click += OnCompleteCheckBoxClick;
        }

        if (OpenDetailButton is not null)
        {
            OpenDetailButton.Click += OnOpenDetailButtonClick;
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
        if (DataContext is TaskRowViewModel vm)
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
        if (DataContext is TaskRowViewModel vm)
        {
            OpenDetailClicked?.Invoke(vm.Id);
            if (FindParentPanelViewModel() is { } panelVm)
            {
                panelVm.RowInteraction.HandleRowClick(vm.Id, 2);
            }
            e.Handled = true;
        }
    }
}
