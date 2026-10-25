using System.Text;
using DesktopNotes.Application.Attachments;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Records;
using DesktopNotes.Domain;
using DesktopNotes.Infrastructure.Files;
using DesktopNotes.Infrastructure.Persistence;
using DesktopNotes.Platforms.Windows;
using Xunit;

namespace DesktopNotes.Integration.Tests;

public class AttachmentMoveTests
{
    private static readonly TimeZoneInfo TestZone = TimeZoneInfo.CreateCustomTimeZone(
        "TestUTC8", TimeSpan.FromHours(8), "TestUTC8", "TestUTC8");

    [Fact]
    public async Task Drag_always_moves_and_clipboard_respects_copy_cut_unknown_and_token_isolation()
    {
        using var fixture = new TempDirectoryFixture();
        using var store = new SqliteStore(":memory:");
        var recordService = new RecordService(store, zoneProvider: () => TestZone);
        var cleanup = new WindowsSourceCleanup();
        var fileStore = new ManagedFileStore(cleanup);
        var worker = new ImportWorker(store, fileStore);
        var recovery = new RecoveryService(store, fileStore, worker);
        var clipboard = new WindowsFileClipboard();
        var attachmentService = new AttachmentService(store, fileStore, worker, recovery, fixture.StorageRootDir, clipboard);

        Guid taskId = Guid.NewGuid();
        await recordService.SaveAsync(new SaveRecordRequest(
            taskId, 0, RecordKind.Task, null, "拖放与剪贴板测试", "",
            new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
            InheritField.None), CancellationToken.None);

        // 1. Drag origin always moves even if Copy intent was passed (e.g. Ctrl modifier held)
        string dragFile = Path.Combine(fixture.SourceDir, "dragged.txt");
        File.WriteAllText(dragFile, "Dragged file content", Encoding.UTF8);

        await attachmentService.EnqueueAsync(new ImportRequest(
            Guid.NewGuid(), taskId, new[] { dragFile }, ImportIntent.Copy, ImportOrigin.Drag), CancellationToken.None);
        Assert.False(File.Exists(dragFile));

        // 2. Clipboard Copy preserves source
        string copyFile = Path.Combine(fixture.SourceDir, "copied.txt");
        File.WriteAllText(copyFile, "Copied file content", Encoding.UTF8);
        clipboard.SetClipboardFiles(new[] { copyFile }, ImportIntent.Copy);

        await attachmentService.PasteFromClipboardAsync(taskId, CancellationToken.None);
        Assert.True(File.Exists(copyFile));
        Assert.Empty(clipboard.AcknowledgedMoveTokens);

        // 3. Clipboard with unknown cut marker (null) defaults to Copy and preserves source
        string unknownFile = Path.Combine(fixture.SourceDir, "unknown_effect.txt");
        File.WriteAllText(unknownFile, "Unknown effect content", Encoding.UTF8);
        clipboard.SetClipboardFiles(new[] { unknownFile }, preferredDropEffectIntent: null);

        await attachmentService.PasteFromClipboardAsync(taskId, CancellationToken.None);
        Assert.True(File.Exists(unknownFile));

        // 4. Plain text path in clipboard is ignored by file attachment paste
        clipboard.SetClipboardText(unknownFile);
        var plainTextPasteJobs = await attachmentService.PasteFromClipboardAsync(taskId, CancellationToken.None);
        Assert.Empty(plainTextPasteJobs);

        // 5. Clipboard Cut (Move) removes source and acknowledges token
        string cutFile = Path.Combine(fixture.SourceDir, "cut.txt");
        File.WriteAllText(cutFile, "Cut file content", Encoding.UTF8);
        string cutToken = clipboard.SetClipboardFiles(new[] { cutFile }, ImportIntent.Move);

        await attachmentService.PasteFromClipboardAsync(taskId, CancellationToken.None);
        Assert.False(File.Exists(cutFile));
        Assert.Contains(cutToken, clipboard.AcknowledgedMoveTokens);

        // 6. If user copies new text during transfer, new clipboard content is NOT overwritten by move acknowledgement
        string cutFile2 = Path.Combine(fixture.SourceDir, "cut2.txt");
        File.WriteAllText(cutFile2, "Cut file 2 content", Encoding.UTF8);
        clipboard.SetClipboardFiles(new[] { cutFile2 }, ImportIntent.Move);

        string newTextToken = string.Empty;
        fileStore.OnDuringCopyHook = _ =>
        {
            newTextToken = clipboard.SetClipboardText("用户在传输期间新复制的正文");
        };

        await attachmentService.PasteFromClipboardAsync(taskId, CancellationToken.None);
        fileStore.OnDuringCopyHook = null;

        Assert.False(File.Exists(cutFile2));
        Assert.Equal(newTextToken, clipboard.CurrentToken); // New clipboard text preserved!
    }
}
