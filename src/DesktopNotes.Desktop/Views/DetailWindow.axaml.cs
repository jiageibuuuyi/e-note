using Avalonia.Controls;
using Avalonia.Input;
using DesktopNotes.Desktop.ViewModels;
using DesktopNotes.Domain;

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

        if (BtnHeaderClose is not null)
        {
            BtnHeaderClose.Click += (_, _) => Close();
        }

        if (BtnPrio0 is not null)
        {
            BtnPrio0.Click += (_, _) => viewModel.SetOwnPriority(Priority.Highest);
        }

        if (BtnPrio1 is not null)
        {
            BtnPrio1.Click += (_, _) => viewModel.SetOwnPriority(Priority.High);
        }

        if (BtnPrio2 is not null)
        {
            BtnPrio2.Click += (_, _) => viewModel.SetOwnPriority(Priority.Normal);
        }

        if (BtnPrio3 is not null)
        {
            BtnPrio3.Click += (_, _) => viewModel.SetOwnPriority(Priority.Low);
        }

        if (BtnDueToday is not null)
        {
            BtnDueToday.Click += (_, _) => viewModel.SetDueToday();
        }

        if (BtnDueTomorrow is not null)
        {
            BtnDueTomorrow.Click += (_, _) => viewModel.SetDueTomorrow();
        }

        if (BtnDueNextWeek is not null)
        {
            BtnDueNextWeek.Click += (_, _) => viewModel.SetDueNextWeek();
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
