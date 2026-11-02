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

    [Fact]
    public void TaskRowViewModel_due_date_and_visual_style_boundary_tests()
    {
        var localizer = DesktopNotes.Desktop.Localization.AppLocalizer.Instance;
        var now = DateTimeOffset.UtcNow;
        var zone = TimeZoneInfo.Utc;

        var emptyOwn = new EffectiveFields(null, "李工", Priority.Normal, null, DueSpec.Empty, null);
        var taskViewNoDue = new RecordView(
            Guid.NewGuid(), null, RecordKind.Task, "无截止期任务", "正文",
            RecordStatus.Active, emptyOwn, emptyOwn, InheritField.None, 1, now, null, null);

        var dueSpec = new DueSpec(DueKind.Date, new DateOnly(2026, 11, 5), null, null);
        var dueOwn = emptyOwn with { Due = dueSpec };
        var taskViewWithDue = new RecordView(
            Guid.NewGuid(), null, RecordKind.Task, "有截止期任务", "正文",
            RecordStatus.Active, dueOwn, dueOwn, InheritField.None, 1, now, null, null);

        var subtaskView = new RecordView(
            Guid.NewGuid(), taskViewNoDue.Id, RecordKind.Task, "子任务", "正文",
            RecordStatus.Active, emptyOwn, emptyOwn, InheritField.None, 1, now, null, null);

        // 1. Verify HasDue behavior: task without deadline MUST NOT show due date pill!
        var vmNoDue = new TaskRowViewModel(taskViewNoDue, zone, now, false);
        Assert.False(vmNoDue.HasDue);

        var vmWithDue = new TaskRowViewModel(taskViewWithDue, zone, now, false);
        Assert.True(vmWithDue.HasDue);
        Assert.Contains("2026-11-05", vmWithDue.DueSummary);

        // 2. Test Minimalist Style boundary
        localizer.SetVisualStyle(DesktopNotes.Desktop.Localization.AppVisualStyle.Minimalist);
        var subVmMin = new TaskRowViewModel(subtaskView, zone, now, false);
        Assert.True(subVmMin.ShowDragHandle == false); // Subtasks don't show drag handle
        Assert.True(vmNoDue.ShowDragHandle); // Root task shows 6-dot drag handle in Minimalist mode
        Assert.True(subVmMin.ShowMinimalistSubtaskBranch);
        Assert.False(subVmMin.ShowRealisticSubtaskPlant);

        // 3. Test Realistic Style boundary
        localizer.SetVisualStyle(DesktopNotes.Desktop.Localization.AppVisualStyle.Realistic);
        var subVmReal = new TaskRowViewModel(subtaskView, zone, now, false);
        Assert.False(vmNoDue.ShowDragHandle); // 6-dot drag handle MUST be hidden in Realistic mode!
        Assert.False(subVmReal.ShowMinimalistSubtaskBranch);
        Assert.True(subVmReal.ShowRealisticSubtaskPlant);
        Assert.Equal("🌱", subVmReal.SubtaskPlantIcon);
        Assert.True(subVmReal.ShowSubtaskAssigneeInline);

        // Complete the subtask -> sprout becomes flower
        subVmReal.IsMarkedCompleted = true;
        Assert.Equal("🌸", subVmReal.SubtaskPlantIcon);
        Assert.False(subVmReal.ShowSubtaskAssigneeInline);
    }

    [Fact]
    public async Task MultiLevelSubtasks_Support_Arbitrary_Depth_And_Tree_Interactions()
    {
        using var store = new SqliteStore(":memory:");
        var recordService = new RecordService(store, zoneProvider: () => TestZone);
        var queryService = new RecordQueryService(store, () => TestZone);
        var panelVm = new PanelViewModel(recordService, queryService, zoneProvider: () => TestZone);

        var emptyOwn = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);

        // Level 0: Root task
        Guid rootId = Guid.NewGuid();
        await recordService.SaveAsync(new SaveRecordRequest(
            rootId, 0, RecordKind.Task, null, "根任务-L0", "根任务说明",
            emptyOwn, InheritField.None), CancellationToken.None);

        // Level 1: Child task
        Guid childId = Guid.NewGuid();
        await recordService.SaveAsync(new SaveRecordRequest(
            childId, 0, RecordKind.Task, rootId, "子任务-L1", "子任务说明",
            emptyOwn, InheritField.All), CancellationToken.None);

        // Level 2: Grandchild task
        Guid grandchildId = Guid.NewGuid();
        await recordService.SaveAsync(new SaveRecordRequest(
            grandchildId, 0, RecordKind.Task, childId, "孙任务-L2", "孙任务说明",
            emptyOwn, InheritField.All), CancellationToken.None);

        // Level 3: Great-grandchild task
        Guid greatGrandchildId = Guid.NewGuid();
        await recordService.SaveAsync(new SaveRecordRequest(
            greatGrandchildId, 0, RecordKind.Task, grandchildId, "曾孙任务-L3", "曾孙说明",
            emptyOwn, InheritField.All), CancellationToken.None);

        await panelVm.RefreshAsync(CancellationToken.None);

        Assert.Equal(4, panelVm.Items.Count);
        var r0 = panelVm.Items[0];
        var r1 = panelVm.Items[1];
        var r2 = panelVm.Items[2];
        var r3 = panelVm.Items[3];

        Assert.Equal(rootId, r0.Id);
        Assert.Equal(0, r0.Depth);
        Assert.False(r0.IsSubtask);
        Assert.True(r0.CanAddSubtask);
        Assert.Equal(1, r0.SubtaskCount);

        Assert.Equal(childId, r1.Id);
        Assert.Equal(1, r1.Depth);
        Assert.True(r1.IsSubtask);
        Assert.True(r1.CanAddSubtask);
        Assert.Equal(1, r1.SubtaskCount);

        Assert.Equal(grandchildId, r2.Id);
        Assert.Equal(2, r2.Depth);
        Assert.True(r2.IsSubtask);
        Assert.True(r2.CanAddSubtask);
        Assert.Equal(1, r2.SubtaskCount);

        Assert.Equal(greatGrandchildId, r3.Id);
        Assert.Equal(3, r3.Depth);
        Assert.True(r3.IsSubtask);
        Assert.True(r3.CanAddSubtask);
        Assert.Equal(0, r3.SubtaskCount);

        // Verify indent margins increase with depth
        Assert.True(r1.RowMargin.Left > r0.RowMargin.Left);
        Assert.True(r2.RowMargin.Left > r1.RowMargin.Left);
        Assert.True(r3.RowMargin.Left > r2.RowMargin.Left);

        // Test collapse on Level 1: should collapse grandchild and great-grandchild, but keep root and child
        await panelVm.ToggleTreeCollapseAsync(childId, CancellationToken.None);
        Assert.Equal(2, panelVm.Items.Count);
        Assert.Equal(rootId, panelVm.Items[0].Id);
        Assert.Equal(childId, panelVm.Items[1].Id);

        // Re-expand Level 1
        await panelVm.ToggleTreeCollapseAsync(childId, CancellationToken.None);
        Assert.Equal(4, panelVm.Items.Count);

        // Test DetailViewModel can have subtasks on subtask
        var detailVm = new DetailViewModel(recordService, queryService);
        await detailVm.LoadAsync(childId, CancellationToken.None);
        Assert.True(detailVm.CanHaveSubtasks);
        Assert.True(detailVm.HasSubtasks);
        Assert.Contains(detailVm.Subtasks, x => x.Id == grandchildId);
    }

    [Fact]
    public async Task EmptyTrash_Permanently_Purges_Records_And_Clears_Trash_List()
    {
        using var store = new SqliteStore(":memory:");
        var recordService = new RecordService(store, zoneProvider: () => TestZone);
        var queryService = new RecordQueryService(store, () => TestZone);
        var calVm = new CalendarViewModel(queryService, recordService);

        var emptyOwn = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);

        // Create tasks
        Guid task1 = Guid.NewGuid();
        Guid task2 = Guid.NewGuid();
        await recordService.SaveAsync(new SaveRecordRequest(
            task1, 0, RecordKind.Task, null, "即将删除的任务1", "正文1",
            emptyOwn, InheritField.None), CancellationToken.None);
        await recordService.SaveAsync(new SaveRecordRequest(
            task2, 0, RecordKind.Task, null, "即将删除的任务2", "正文2",
            emptyOwn, InheritField.None), CancellationToken.None);

        // Move to trash
        var t1 = (await recordService.GetAsync(task1, CancellationToken.None))!;
        var t2 = (await recordService.GetAsync(task2, CancellationToken.None))!;
        await recordService.TrashAsync(task1, t1.Version, CancellationToken.None);
        await recordService.TrashAsync(task2, t2.Version, CancellationToken.None);

        // Switch calendar view to Trash
        await calVm.SetHistoryModeAsync(showTrash: true, CancellationToken.None);
        Assert.True(calVm.IsTrashView);
        Assert.True(calVm.HasHistoricalItems);
        Assert.Equal(2, calVm.HistoricalItems.Count);

        // Empty Trash via ViewModel
        int purged = await calVm.EmptyTrashAsync(CancellationToken.None);
        Assert.Equal(2, purged);

        // Verify trash is now empty
        Assert.False(calVm.HasHistoricalItems);
        Assert.Empty(calVm.HistoricalItems);

        // Verify recordService cannot get purged records
        var purged1 = await recordService.GetAsync(task1, CancellationToken.None);
        var purged2 = await recordService.GetAsync(task2, CancellationToken.None);
        Assert.Null(purged1);
        Assert.Null(purged2);
    }

    [Fact]
    public async Task ConvertTaskToSubtask_Prevents_Cycles_Across_Multi_Level_Hierarchy()
    {
        using var store = new SqliteStore(":memory:");
        var recordService = new RecordService(store, zoneProvider: () => TestZone);
        var queryService = new RecordQueryService(store, () => TestZone);
        var panelVm = new PanelViewModel(recordService, queryService, zoneProvider: () => TestZone);

        var emptyOwn = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);

        // A -> B -> C
        Guid idA = Guid.NewGuid();
        Guid idB = Guid.NewGuid();
        Guid idC = Guid.NewGuid();

        await recordService.SaveAsync(new SaveRecordRequest(
            idA, 0, RecordKind.Task, null, "任务A", "", emptyOwn, InheritField.None), CancellationToken.None);
        await recordService.SaveAsync(new SaveRecordRequest(
            idB, 0, RecordKind.Task, idA, "任务B", "", emptyOwn, InheritField.All), CancellationToken.None);
        await recordService.SaveAsync(new SaveRecordRequest(
            idC, 0, RecordKind.Task, idB, "任务C", "", emptyOwn, InheritField.All), CancellationToken.None);

        // Attempting to move A into C must fail due to cycle!
        await panelVm.ConvertTaskToSubtaskAsync(idA, idC, CancellationToken.None);

        var recA = (await recordService.GetAsync(idA, CancellationToken.None))!;
        Assert.Null(recA.ParentId); // A must remain root!
    }

    [Fact]
    public async Task TaskRow_ClickSelection_Toggles_Realistic_And_Minimalist_Delete_Buttons()
    {
        using var store = new SqliteStore(":memory:");
        var recordService = new RecordService(store, zoneProvider: () => TestZone);
        var queryService = new RecordQueryService(store, () => TestZone);
        var panelVm = new PanelViewModel(recordService, queryService, zoneProvider: () => TestZone);

        var emptyOwn = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);
        Guid taskId = Guid.NewGuid();
        await recordService.SaveAsync(new SaveRecordRequest(
            taskId, 0, RecordKind.Task, null, "测试点击显示删除按钮", "", emptyOwn, InheritField.None), CancellationToken.None);

        await panelVm.RefreshAsync(CancellationToken.None);
        var item = Assert.Single(panelVm.Items);

        // Initially not expanded: delete buttons hidden
        item.IsExpanded = false;
        Assert.False(item.ShowRealisticDelete);
        Assert.False(item.ShowMinimalistDelete);

        // Click to expand row:
        item.IsExpanded = true;
        if (DesktopNotes.Desktop.Localization.AppLocalizer.Instance.IsRealisticStyle)
        {
            Assert.True(item.ShowRealisticDelete);
            Assert.False(item.ShowMinimalistDelete);
        }
        else
        {
            Assert.False(item.ShowRealisticDelete);
            Assert.True(item.ShowMinimalistDelete);
        }

        // Switch to Minimalist style
        DesktopNotes.Desktop.Localization.AppLocalizer.Instance.SetVisualStyle(
            DesktopNotes.Desktop.Localization.AppVisualStyle.Minimalist);
        item.RefreshLocalization();
        Assert.False(item.ShowRealisticDelete);
        Assert.True(item.ShowMinimalistDelete);

        // Switch to Realistic style
        DesktopNotes.Desktop.Localization.AppLocalizer.Instance.SetVisualStyle(
            DesktopNotes.Desktop.Localization.AppVisualStyle.Realistic);
        item.RefreshLocalization();
        Assert.True(item.ShowRealisticDelete);
        Assert.False(item.ShowMinimalistDelete);
    }

    [Fact]
    public async Task Accidental_Subtask_Reparenting_And_Promotion_Can_Be_Undone()
    {
        using var store = new SqliteStore(":memory:");
        var recordService = new RecordService(store, zoneProvider: () => TestZone);
        var queryService = new RecordQueryService(store, () => TestZone);
        var panelVm = new PanelViewModel(recordService, queryService, zoneProvider: () => TestZone);

        var emptyOwn = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);
        Guid parentId = Guid.NewGuid();
        Guid taskToDrag = Guid.NewGuid();

        await recordService.SaveAsync(new SaveRecordRequest(
            parentId, 0, RecordKind.Task, null, "主任务目标", "", emptyOwn, InheritField.None), CancellationToken.None);
        await recordService.SaveAsync(new SaveRecordRequest(
            taskToDrag, 0, RecordKind.Task, null, "误拖入任务", "", emptyOwn, InheritField.None), CancellationToken.None);

        await panelVm.RefreshAsync(CancellationToken.None);

        // User accidentally drags taskToDrag onto parentId
        await panelVm.ConvertTaskToSubtaskAsync(taskToDrag, parentId, CancellationToken.None);

        var draggedRec = await recordService.GetAsync(taskToDrag, CancellationToken.None);
        Assert.NotNull(draggedRec);
        Assert.Equal(parentId, draggedRec.ParentId);

        // User triggers Undo (Ctrl+Z or Settings Undo Button)
        Assert.True(DesktopNotes.Desktop.Services.UndoService.Instance.CanUndo);
        bool undoOk = await panelVm.UndoLastActionAsync(CancellationToken.None);
        Assert.True(undoOk);

        // Task must be restored back to root!
        var restoredRec = await recordService.GetAsync(taskToDrag, CancellationToken.None);
        Assert.NotNull(restoredRec);
        Assert.Null(restoredRec.ParentId);

        // Now test PromoteSubtaskToRootAsync followed by Undo
        // Re-convert to subtask
        await panelVm.ConvertTaskToSubtaskAsync(taskToDrag, parentId, CancellationToken.None);
        var subRec = await recordService.GetAsync(taskToDrag, CancellationToken.None);
        Assert.Equal(parentId, subRec!.ParentId);

        // Promote to root
        await panelVm.PromoteSubtaskToRootAsync(taskToDrag, CancellationToken.None);
        var promotedRec = await recordService.GetAsync(taskToDrag, CancellationToken.None);
        Assert.Null(promotedRec!.ParentId);

        // Undo promotion: should reparent back to parentId
        bool undoPromoteOk = await panelVm.UndoLastActionAsync(CancellationToken.None);
        Assert.True(undoPromoteOk);
        var reSubRec = await recordService.GetAsync(taskToDrag, CancellationToken.None);
        Assert.Equal(parentId, reSubRec!.ParentId);
    }
    [Fact]
    public void English_Localization_Uses_TODO_Instead_Of_Task()
    {
        var loc = DesktopNotes.Desktop.Localization.AppLocalizer.Instance;
        loc.SetLanguage(DesktopNotes.Desktop.Localization.AppLanguage.English);

        // Check main panel and navigation strings
        Assert.Contains("TODO", loc.KindSwitchTooltip);
        Assert.Contains("TODO", loc.MenuSwitchTasks);
        Assert.Contains("TODO", loc.RowAddSubtaskBtn);
        Assert.Contains("TODO", loc.RowAddSubtaskTooltip);
        Assert.Contains("TODO", loc.RowMenuAddSubtask);
        Assert.Contains("TODO", loc.RowMenuPromoteSubtask);
        Assert.Contains("TODO", loc.RowEditTooltip);
        Assert.Contains("TODO", loc.RowDeleteTooltip);
        Assert.Contains("TODO", loc.MenuPromoteToRoot);
        Assert.Contains("TODO", loc.NoticePromotedToRoot);
        Assert.Contains("TODO", loc.DetailSubtasksLabel);
        Assert.Contains("TODO", loc.TabCalendar);
        Assert.Contains("TODO", loc.CalEmptyTitle);
    }

    [Fact]
    public async Task CalendarViewModel_MonthGrid_Populates_Created_And_Completed_Task_Markers()
    {
        using var store = new SqliteStore(":memory:");
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.FromHours(8)));
        var recordService = new RecordService(store, clock, () => TestZone);
        var queryService = new RecordQueryService(store, () => TestZone);
        var calVm = new CalendarViewModel(queryService, recordService, () => TestZone);

        var emptyOwn = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);

        // Task 1: created on Nov 15, 2026
        Guid task1 = Guid.NewGuid();
        await recordService.SaveAsync(new SaveRecordRequest(
            task1, 0, RecordKind.Task, null, "11月15日创建的任务", "详情1", emptyOwn, InheritField.None), CancellationToken.None);

        // Task 2: created on Nov 15 and completed on Nov 16
        Guid task2 = Guid.NewGuid();
        await recordService.SaveAsync(new SaveRecordRequest(
            task2, 0, RecordKind.Task, null, "11月16日完成的任务", "详情2", emptyOwn, InheritField.None), CancellationToken.None);

        clock.SetUtcNow(new DateTimeOffset(2026, 11, 16, 15, 30, 0, TimeSpan.FromHours(8)));
        var rec2 = (await recordService.GetAsync(task2, CancellationToken.None))!;
        await recordService.CloseAsync(task2, rec2.Version, false, CancellationToken.None);

        // Load November 2026
        await calVm.LoadMonthAsync(2026, 11, CalendarAxis.Created, RecordKind.Task, null, CancellationToken.None);

        Assert.Equal(42, calVm.MonthDays.Count);
        Assert.True(calVm.IsMonthGridView);

        // Day 15: must have 2 created tasks
        var day15 = calVm.MonthDays.FirstOrDefault(x => x.Date == new DateOnly(2026, 11, 15));
        Assert.NotNull(day15);
        Assert.True(day15.HasCreatedTasks);
        Assert.Equal(2, day15.CreatedCount);
        Assert.False(day15.HasCompletedTasks);

        // Day 16: must have 1 completed task
        var day16 = calVm.MonthDays.FirstOrDefault(x => x.Date == new DateOnly(2026, 11, 16));
        Assert.NotNull(day16);
        Assert.True(day16.HasCompletedTasks);
        Assert.Equal(1, day16.CompletedCount);

        // Select Day 15 -> SelectedDayItems should contain task1 and task2
        calVm.SelectDay(day15);
        Assert.Equal(new DateOnly(2026, 11, 15), calVm.SelectedDay);
        Assert.True(day15.IsSelected);
        Assert.True(calVm.HasSelectedDayItems);
        Assert.Contains(calVm.SelectedDayItems, x => x.Id == task1);
        Assert.Contains(calVm.SelectedDayItems, x => x.Id == task2);

        // Floating window hover content assertions
        Assert.Contains("2026", day15.DayHeaderTitle);
        Assert.NotEmpty(day15.CreatedSectionTitle);
        Assert.NotEmpty(day16.CompletedSectionTitle);
    }
    [Fact]
    public async Task RealisticPlant_Displays_GreenSprout_WhenPending_And_PinkFlower_WhenCompleted()
    {
        using var store = new SqliteStore(":memory:");
        var recordService = new RecordService(store, zoneProvider: () => TestZone);
        var queryService = new RecordQueryService(store, () => TestZone);
        var panelVm = new PanelViewModel(recordService, queryService, zoneProvider: () => TestZone);

        var loc = DesktopNotes.Desktop.Localization.AppLocalizer.Instance;
        loc.SetVisualStyle(DesktopNotes.Desktop.Localization.AppVisualStyle.Realistic);

        var emptyOwn = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);
        Guid parentId = Guid.NewGuid();
        await recordService.SaveAsync(new SaveRecordRequest(
            parentId, 0, RecordKind.Task, null, "主任务测试", "详情", emptyOwn, InheritField.None), CancellationToken.None);

        Guid subId = Guid.NewGuid();
        await recordService.SaveAsync(new SaveRecordRequest(
            subId, 0, RecordKind.Task, parentId, "子任务测试", "详情", emptyOwn, InheritField.All), CancellationToken.None);

        await panelVm.RefreshAsync(CancellationToken.None);

        var mainRow = panelVm.Items.First(x => x.Id == parentId);
        var subRow = panelVm.Items.First(x => x.Id == subId);

        // In realistic mode:
        // 1. Both main tasks and subtasks show realistic plant toggle
        Assert.True(mainRow.ShowRealisticPlantToggle);
        Assert.True(subRow.ShowRealisticPlantToggle);

        // 2. Uncompleted state: must be green sprout 🌱 (绿色的嫩苗)
        Assert.Equal("🌱", mainRow.RealisticPlantIcon);
        Assert.Equal("🌱", subRow.RealisticPlantIcon);
        Assert.Equal("🌱", subRow.SubtaskPlantIcon);
        Assert.True(mainRow.RealisticPlantTooltip.Contains("嫩苗") || mainRow.RealisticPlantTooltip.Contains("sprout"));

        // 3. Toggle parent task to completed: parent and its subtask both bloom into pink flower 🌸 (粉色的小花)
        panelVm.ToggleCompletedMark(parentId);

        Assert.Equal("🌸", mainRow.RealisticPlantIcon);
        Assert.Equal("🌸", subRow.RealisticPlantIcon);
        Assert.Equal("🌸", subRow.SubtaskPlantIcon);
        Assert.True(mainRow.RealisticPlantTooltip.Contains("小花") || mainRow.RealisticPlantTooltip.Contains("flower"));

        // 4. Toggle subtask back to active: unchecking child cascades to uncheck parent, reverting back to green sprout 🌱
        panelVm.ToggleCompletedMark(subId);
        Assert.Equal("🌱", subRow.RealisticPlantIcon);
        Assert.Equal("🌱", mainRow.RealisticPlantIcon);

        // 4. In minimalist mode: realistic plant toggle is hidden
        loc.SetVisualStyle(DesktopNotes.Desktop.Localization.AppVisualStyle.Minimalist);
        mainRow.RefreshLocalization();
        subRow.RefreshLocalization();
        Assert.False(mainRow.ShowRealisticPlantToggle);
        Assert.False(subRow.ShowRealisticPlantToggle);
        Assert.True(subRow.ShowMinimalistSubtaskBranch);
    }

    [Fact]
    public void TaskRow_ContextMenu_Includes_AddSubtask_And_EditContent()
    {
        var loc = DesktopNotes.Desktop.Localization.AppLocalizer.Instance;

        // Chinese
        loc.SetLanguage(DesktopNotes.Desktop.Localization.AppLanguage.Chinese);
        Assert.Equal("➕  添加子任务", loc.RowMenuAddSubtask);
        Assert.Equal("✏️  修改内容", loc.RowMenuEdit);
        Assert.Equal("📋  复制文字", loc.RowMenuCopyText);
        Assert.Equal("📋  粘贴文字", loc.RowMenuPasteText);

        // English
        loc.SetLanguage(DesktopNotes.Desktop.Localization.AppLanguage.English);
        Assert.Equal("➕  Add Sub-TODO", loc.RowMenuAddSubtask);
        Assert.Equal("✏️  Edit Content", loc.RowMenuEdit);
        Assert.Equal("📋  Copy Text", loc.RowMenuCopyText);
        Assert.Equal("📋  Paste Text", loc.RowMenuPasteText);
    }
    [Fact]
    public void ContextMenu_Separation_TopHeader_Is_ApplicationSettings_And_TaskRow_Is_TaskSettings()
    {
        var loc = DesktopNotes.Desktop.Localization.AppLocalizer.Instance;
        loc.SetLanguage(DesktopNotes.Desktop.Localization.AppLanguage.Chinese);

        // Task context menu strictly contains task-related actions
        var taskItems = new[]
        {
            loc.RowMenuAddSubtask,
            loc.RowMenuEdit,
            loc.RowMenuPromoteSubtask,
            loc.RowMenuOpenDetail,
            loc.RowMenuArchive,
            loc.RowMenuTrash
        };

        // App header menu strictly contains application-level settings
        var appItems = new[]
        {
            loc.MenuUndoAction,
            loc.MenuStyleHeader,
            loc.MenuSortHeader,
            loc.MenuLanguageHeader,
            loc.MenuSwitchTasks,
            loc.MenuSwitchNotes,
            loc.MenuSwitchTheme,
            loc.MenuOpenSettings,
            loc.MenuCalendarArchive,
            loc.MenuArchiveAllDone,
            loc.MenuHideToTray,
            loc.MenuExitApp
        };

        // Ensure strict isolation: None of the application menu items are inside task menu
        foreach (var appItem in appItems)
        {
            Assert.DoesNotContain(appItem, taskItems);
        }

        // None of the task actions are inside application menu
        foreach (var taskItem in taskItems)
        {
            Assert.DoesNotContain(taskItem, appItems);
        }
    }
}
