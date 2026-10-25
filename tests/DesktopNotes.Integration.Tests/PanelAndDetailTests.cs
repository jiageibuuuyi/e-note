using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Queries;
using DesktopNotes.Application.Records;
using DesktopNotes.Desktop.Interaction;
using DesktopNotes.Desktop.ViewModels;
using DesktopNotes.Domain;
using DesktopNotes.Infrastructure.Persistence;
using Xunit;

namespace DesktopNotes.Integration.Tests;

public class PanelAndDetailTests
{
    private static readonly TimeZoneInfo TestZone = TimeZoneInfo.CreateCustomTimeZone(
        "TestUTC8", TimeSpan.FromHours(8), "TestUTC8", "TestUTC8");

    [Fact]
    public async Task PanelViewModel_handles_1000_tasks_pagination_search_note_isolation_and_archiving()
    {
        using var store = new SqliteStore(":memory:");
        var recordService = new RecordService(store, zoneProvider: () => TestZone);
        var queryService = new RecordQueryService(store, () => TestZone);
        var panelVm = new PanelViewModel(recordService, queryService, zoneProvider: () => TestZone);

        var emptyOwn = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);
        Guid parentId = Guid.NewGuid();

        await recordService.SaveAsync(new SaveRecordRequest(
            parentId, 0, RecordKind.Task, null, "", new string('甲', 35) + "50%_尾部匹配",
            emptyOwn, InheritField.None), CancellationToken.None);

        Guid subtaskId = Guid.NewGuid();
        await recordService.SaveAsync(new SaveRecordRequest(
            subtaskId, 0, RecordKind.Task, parentId, "独立可见子任务", "合同细节",
            emptyOwn, InheritField.All), CancellationToken.None);

        // Insert 998 more tasks -> 1000 total active tasks
        for (int i = 0; i < 998; i++)
        {
            await recordService.SaveAsync(new SaveRecordRequest(
                Guid.NewGuid(), 0, RecordKind.Task, null, $"任务-{i:D4}", $"正文-{i}",
                emptyOwn, InheritField.None), CancellationToken.None);
        }

        // Insert 1 Note -> should not mix into Task view
        Guid noteId = Guid.NewGuid();
        await recordService.SaveAsync(new SaveRecordRequest(
            noteId, 0, RecordKind.Note, null, "独立笔记", "笔记内容",
            emptyOwn, InheritField.None), CancellationToken.None);

        await panelVm.RefreshAsync(CancellationToken.None);
        Assert.Equal(1000, panelVm.TotalCount);
        Assert.Equal(100, panelVm.Items.Count);

        while (panelVm.Items.Count < panelVm.TotalCount)
        {
            await panelVm.LoadMoreAsync(CancellationToken.None);
        }
        Assert.Equal(1000, panelVm.Items.Count);
        Assert.Contains(panelVm.Items, x => x.Id == subtaskId && x.IsSubtask);

        // Untitled long body name generated to 30 chars + ellipsis
        var parentRow = panelVm.Items.Single(x => x.Id == parentId);
        Assert.Equal(new string('甲', 30) + "…", parentRow.DisplayTitle);

        // Literal search for "%_" beyond 30th char
        panelVm.SearchText = "%_";
        await panelVm.RefreshAsync(CancellationToken.None);
        Assert.Single(panelVm.Items);
        Assert.Equal(parentId, panelVm.Items[0].Id);

        // Clear search restores 1000 total count
        await panelVm.ClearSearchAsync();
        Assert.Equal(1000, panelVm.TotalCount);

        // Switch to Note tab -> only the 1 note appears
        panelVm.CurrentKind = RecordKind.Note;
        await panelVm.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, panelVm.TotalCount);
        Assert.Equal(noteId, panelVm.Items.Single().Id);

        // Switch back to Task tab and complete subtask -> 999 active tasks remain
        panelVm.CurrentKind = RecordKind.Task;
        await panelVm.RefreshAsync(CancellationToken.None);
        await panelVm.CompleteRecordAsync(subtaskId, includeChildren: false, CancellationToken.None);
        Assert.Equal(999, panelVm.TotalCount);
    }

    [Fact]
    public async Task TaskRowInteraction_double_click_on_expanded_row_does_not_collapse_before_opening_detail()
    {
        Guid? expandedId = null;
        Guid? openedDetailId = null;
        int expandChangeCount = 0;

        var interaction = new TaskRowInteraction(
            () => expandedId,
            id =>
            {
                expandedId = id;
                expandChangeCount++;
            },
            id => openedDetailId = id,
            doubleClickThresholdOverride: TimeSpan.FromMilliseconds(120));

        var rowId = Guid.NewGuid();

        // Single click expands immediately
        interaction.HandleRowClick(rowId, clickCount: 1);
        Assert.Equal(rowId, expandedId);
        Assert.Equal(1, expandChangeCount);

        // Double click on already-expanded row: first click schedules delayed collapse, second click cancels it and opens detail!
        interaction.HandleRowClick(rowId, clickCount: 1);
        interaction.HandleRowClick(rowId, clickCount: 2);
        await Task.Delay(180);

        Assert.Equal(rowId, expandedId);
        Assert.Equal(rowId, openedDetailId);
        Assert.Equal(1, expandChangeCount);
    }

    [Fact]
    public async Task DetailViewModel_clears_due_inheritance_on_explicit_clear_and_manages_drafts()
    {
        using var store = new SqliteStore(":memory:");
        var recordService = new RecordService(store, zoneProvider: () => TestZone);
        var queryService = new RecordQueryService(store, () => TestZone);

        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var due = new DueSpec(DueKind.Date, new DateOnly(2026, 10, 25), null, null);

        await recordService.SaveAsync(new SaveRecordRequest(
            parentId, 0, RecordKind.Task, null, "Parent", "",
            new EffectiveFields("Pub", "Ass", Priority.High, null, due, null),
            InheritField.None), CancellationToken.None);

        await recordService.SaveAsync(new SaveRecordRequest(
            childId, 0, RecordKind.Task, parentId, "Child", "Initial Body",
            new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
            InheritField.All), CancellationToken.None);

        var detailVm = new DetailViewModel(recordService, queryService);
        await detailVm.LoadAsync(childId, CancellationToken.None);

        Assert.True(detailVm.IsInheriting(InheritField.Due));
        Assert.Equal(due, detailVm.Effective.Due);

        // User clears deadline explicitly -> clears Due inheritance bit and keeps empty due
        detailVm.ClearDue();
        Assert.False(detailVm.IsInheriting(InheritField.Due));
        Assert.Equal(DueSpec.Empty, detailVm.Effective.Due);

        detailVm.Body = "Updated Body";
        await detailVm.FlushDraftNowAsync(CancellationToken.None);
        Assert.NotNull(await recordService.GetDraftAsync(detailVm.EditorId, CancellationToken.None));

        bool saved = await detailVm.SaveAsync(CancellationToken.None);
        Assert.True(saved);
        Assert.Null(await recordService.GetDraftAsync(detailVm.EditorId, CancellationToken.None));
    }
}
