using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is TaskRowViewModel vm)
        {
            RowClicked?.Invoke(vm.Id, e.ClickCount);
            e.Handled = true;
        }
    }

    private void OnCompleteCheckBoxClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TaskRowViewModel vm)
        {
            CompleteRequested?.Invoke(vm.Id);
            e.Handled = true;
        }
    }

    private void OnOpenDetailButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TaskRowViewModel vm)
        {
            OpenDetailClicked?.Invoke(vm.Id);
            e.Handled = true;
        }
    }
}
