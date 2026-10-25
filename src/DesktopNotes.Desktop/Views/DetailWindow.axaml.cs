using Avalonia.Controls;
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

        if (BtnClearDue is not null)
        {
            BtnClearDue.Click += (_, _) => viewModel.ClearDue();
        }

        if (BtnSave is not null)
        {
            BtnSave.Click += async (_, _) => await viewModel.SaveAsync(CancellationToken.None);
        }

        if (BtnClose is not null)
        {
            BtnClose.Click += (_, _) => Close();
        }
    }
}
