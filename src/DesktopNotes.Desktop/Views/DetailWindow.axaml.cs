using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Desktop.Localization;
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
        if (viewModel.IsNoteKind)
        {
            Height = 520;
        }

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

        if (BtnStartToday is not null)
        {
            BtnStartToday.Click += (_, _) => viewModel.SetStartToday();
        }

        if (BtnClearStart is not null)
        {
            BtnClearStart.Click += (_, _) => viewModel.ClearStart();
        }

        if (BtnAddSubtask is not null)
        {
            BtnAddSubtask.Click += async (_, _) => await viewModel.AddSubtaskAsync(CancellationToken.None);
        }

        if (NewSubtaskBox is not null)
        {
            NewSubtaskBox.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    await viewModel.AddSubtaskAsync(CancellationToken.None);
                }
            };
        }

        if (BtnSelectAttachmentFolder is not null)
        {
            BtnSelectAttachmentFolder.Click += async (_, _) =>
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = AppLocalizer.Instance.T("Select folder for attached files", "选择该记录的附件存储目录"),
                    AllowMultiple = false
                });
                if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } localPath)
                {
                    viewModel.SetCustomAttachmentDirectory(localPath);
                }
            };
        }

        if (BtnOpenAttachmentFolder is not null)
        {
            BtnOpenAttachmentFolder.Click += (_, _) => viewModel.OpenAttachmentRootDirectory();
        }

        if (BtnAddAttachmentFiles is not null)
        {
            BtnAddAttachmentFiles.Click += async (_, _) =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = AppLocalizer.Instance.T("Select files to attach", "选择关联到该记录的文件"),
                    AllowMultiple = true
                });
                var paths = files
                    .Select(f => f.TryGetLocalPath())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Cast<string>()
                    .ToArray();
                if (paths.Length > 0)
                {
                    await viewModel.ImportFilesAsync(paths, ImportOrigin.FilePicker, CancellationToken.None);
                }
            };
        }

        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DropEvent, OnWindowDrop);

        if (BtnArchiveDetail is not null)
        {
            BtnArchiveDetail.Click += async (_, _) =>
            {
                if (await viewModel.ArchiveRecordAsync(CancellationToken.None))
                {
                    Close();
                }
            };
        }

        if (BtnTrashDetail is not null)
        {
            BtnTrashDetail.Click += async (_, _) =>
            {
                if (await viewModel.TrashRecordAsync(CancellationToken.None))
                {
                    Close();
                }
            };
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

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Move | DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void OnWindowDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not DetailViewModel vm)
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
            await vm.ImportFilesAsync(localPaths, ImportOrigin.Drag, CancellationToken.None);
        }
    }

    private async void OnOpenAttachmentItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid attachmentId } && DataContext is DetailViewModel vm)
        {
            await vm.OpenAttachmentAsync(attachmentId, CancellationToken.None);
        }
    }

    private async void OnRemoveAttachmentItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid attachmentId } && DataContext is DetailViewModel vm)
        {
            await vm.RemoveAttachmentAsync(attachmentId, CancellationToken.None);
        }
    }

    private async void OnCompleteSubtaskItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid subtaskId } && DataContext is DetailViewModel vm)
        {
            await vm.CompleteSubtaskAsync(subtaskId, CancellationToken.None);
        }
    }

    private async void OnRemoveSubtaskItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid subtaskId } && DataContext is DetailViewModel vm)
        {
            await vm.RemoveSubtaskAsync(subtaskId, CancellationToken.None);
        }
    }
}
