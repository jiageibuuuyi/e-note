using Avalonia.Controls;
using DesktopNotes.Desktop.ViewModels;
using DesktopNotes.Domain;

namespace DesktopNotes.Desktop.Views;

public partial class PanelWindow : Window
{
    public PanelWindow()
    {
        InitializeComponent();
    }

    public PanelWindow(PanelViewModel viewModel) : this()
    {
        DataContext = viewModel;

        if (BtnTabTasks is not null)
        {
            BtnTabTasks.Click += (_, _) => viewModel.CurrentKind = RecordKind.Task;
        }

        if (BtnTabNotes is not null)
        {
            BtnTabNotes.Click += (_, _) => viewModel.CurrentKind = RecordKind.Note;
        }

        if (BtnClearSearch is not null)
        {
            BtnClearSearch.Click += async (_, _) => await viewModel.ClearSearchAsync();
        }

        if (BtnSubmitQuick is not null)
        {
            BtnSubmitQuick.Click += async (_, _) =>
                await viewModel.QuickCreateAsync(viewModel.QuickInputText, CancellationToken.None);
        }
    }
}
