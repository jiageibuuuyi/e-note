using Avalonia.Controls;
using Avalonia.Input;
using DesktopNotes.Desktop.ViewModels;

namespace DesktopNotes.Desktop.Views;

public partial class DetailWindow : Window
{
    public DetailWindow()
    {
        InitializeComponent();
    }

    public DetailWindow(DetailViewModel viewModel) : this()
    {
        DataContext = viewModel;

        if (DetailHeaderRegion is not null)
        {
            DetailHeaderRegion.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    BeginMoveDrag(e);
                }
            };
        }

        if (BtnClearDue is not null)
        {
            BtnClearDue.Click += (_, _) => viewModel.ClearDue();
        }

        if (BtnSave is not null)
        {
            BtnSave.Click += async (_, _) =>
            {
                if (await viewModel.SaveAsync(CancellationToken.None))
                {
                    Close();
                }
            };
        }

        if (BtnClose is not null)
        {
            BtnClose.Click += (_, _) => Close();
        }

        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (await viewModel.SaveAsync(CancellationToken.None))
                {
                    Close();
                }
                e.Handled = true;
            }
        };
    }
}
