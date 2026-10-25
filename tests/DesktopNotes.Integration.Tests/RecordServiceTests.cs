using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Records;
using DesktopNotes.Domain;
using DesktopNotes.Infrastructure.Persistence;
using Xunit;

namespace DesktopNotes.Integration.Tests;

public class RecordServiceTests
{
    private static readonly TimeZoneInfo TestZone = TimeZoneInfo.CreateCustomTimeZone(
        "TestUTC8", TimeSpan.FromHours(8), "TestUTC8", "TestUTC8");

    [Fact]
    public async Task Completion_freezes_snapshot_and_reopen_restores_live_inheritance()
    {
        using var store = new SqliteStore(":memory:");
        var service = new RecordService(store, zoneProvider: () => TestZone);

        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var due1 = new DueSpec(DueKind.Date, new DateOnly(2026, 10, 25), null, null);
        var due2 = new DueSpec(DueKind.Date, new DateOnly(2026, 11, 15), null, null);

        var p = (await service.SaveAsync(new SaveRecordRequest(
            parentId, 0, RecordKind.Task, null, "Parent", "",
            new EffectiveFields(null, "甲", Priority.High, null, due1, null),
            InheritField.None), CancellationToken.None)).Changed.Single();

        var c = (await service.SaveAsync(new SaveRecordRequest(
            childId, 0, RecordKind.Task, parentId, "Child", "",
            new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
            InheritField.All), CancellationToken.None)).Changed.Single();

        // Trying to close parent while child is active with includeChildren=false fails with ActiveChildren
        var activeEx = await Assert.ThrowsAsync<AppException>(() =>
            service.CloseAsync(parentId, p.Version, includeChildren: false, CancellationToken.None));
        Assert.Equal(ErrorCodes.ActiveChildren, activeEx.Code);
        Assert.Contains(childId, activeEx.AffectedIds);

        // Close child individually
        var closedC = (await service.CloseAsync(childId, c.Version, includeChildren: false, CancellationToken.None)).Changed.Single();
        Assert.Equal(RecordStatus.Closed, closedC.Status);
        Assert.Equal(due1, closedC.Effective.Due);

        // Modify parent deadline to due2 -> closed child still displays frozen due1!
        var updatedP = (await service.SaveAsync(new SaveRecordRequest(
            parentId, p.Version, RecordKind.Task, null, "Parent", "",
            new EffectiveFields(null, "乙", Priority.High, null, due2, null),
            InheritField.None), CancellationToken.None)).Changed.Single();

        var frozenChild = (await service.GetAsync(childId, CancellationToken.None))!;
        Assert.Equal(due1, frozenChild.Effective.Due);
        Assert.Equal("甲", frozenChild.Effective.Assignee);

        // Close parent, then try to reopen child with includeParent=false -> ParentRequired
        await service.CloseAsync(parentId, updatedP.Version, includeChildren: false, CancellationToken.None);
        var parentReqEx = await Assert.ThrowsAsync<AppException>(() =>
            service.ReopenAsync(childId, frozenChild.Version, includeParent: false, CancellationToken.None));
        Assert.Equal(ErrorCodes.ParentRequired, parentReqEx.Code);

        // Reopen child with includeParent=true -> reopens parent first, then child, and child now reflects due2 and "乙"!
        await service.ReopenAsync(childId, frozenChild.Version, includeParent: true, CancellationToken.None);
        var reopenedChild = (await service.GetAsync(childId, CancellationToken.None))!;
        Assert.Equal(RecordStatus.Active, reopenedChild.Status);
        Assert.Equal(due2, reopenedChild.Effective.Due);
        Assert.Equal("乙", reopenedChild.Effective.Assignee);
    }

    [Fact]
    public async Task Trash_and_restore_batch_only_restores_children_deleted_with_parent()
    {
        using var store = new SqliteStore(":memory:");
        var service = new RecordService(store, zoneProvider: () => TestZone);

        var parentId = Guid.NewGuid();
        var child1Id = Guid.NewGuid();
        var child2Id = Guid.NewGuid();
        var emptyOwn = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);

        await service.SaveAsync(new SaveRecordRequest(
            parentId, 0, RecordKind.Task, null, "Parent", "", emptyOwn, InheritField.None), CancellationToken.None);
        var c1 = (await service.SaveAsync(new SaveRecordRequest(
            child1Id, 0, RecordKind.Task, parentId, "Child1", "", emptyOwn, InheritField.All), CancellationToken.None)).Changed.Single();
        await service.SaveAsync(new SaveRecordRequest(
            child2Id, 0, RecordKind.Task, parentId, "Child2", "", emptyOwn, InheritField.All), CancellationToken.None);

        // Trash child1 individually first
        await service.TrashAsync(child1Id, c1.Version, CancellationToken.None);

        // Now trash parent (which also trashes child2 in the same batch)
        var p = (await service.GetAsync(parentId, CancellationToken.None))!;
        var trashedBatch = await service.TrashAsync(parentId, p.Version, CancellationToken.None);
        Assert.Equal(2, trashedBatch.Changed.Count);

        // Restore parent -> should restore parent and child2, but NOT child1
        var pTrashed = (await service.GetAsync(parentId, CancellationToken.None))!;
        await service.RestoreAsync(parentId, pTrashed.Version, includeParent: false, CancellationToken.None);

        var pRestored = (await service.GetAsync(parentId, CancellationToken.None))!;
        var c1After = (await service.GetAsync(child1Id, CancellationToken.None))!;
        var c2After = (await service.GetAsync(child2Id, CancellationToken.None))!;

        Assert.Null(pRestored.DeletedAt);
        Assert.Null(c2After.DeletedAt);
        Assert.NotNull(c1After.DeletedAt);
    }
}
