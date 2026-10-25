using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Records;
using DesktopNotes.Domain;
using DesktopNotes.Infrastructure.Persistence;
using Xunit;

namespace DesktopNotes.Integration.Tests;

public class InheritanceTransactionTests
{
    private static readonly TimeZoneInfo TestZone = TimeZoneInfo.CreateCustomTimeZone(
        "TestUTC8", TimeSpan.FromHours(8), "TestUTC8", "TestUTC8");

    [Fact]
    public async Task Live_inheritance_propagates_to_unoverridden_fields_and_enforces_optimistic_concurrency()
    {
        using var store = new SqliteStore(":memory:");
        var service = new RecordService(store, zoneProvider: () => TestZone);

        var parentId = Guid.NewGuid();
        var childCId = Guid.NewGuid();
        var childDId = Guid.NewGuid();

        var dueOct25 = new DueSpec(DueKind.Date, new DateOnly(2026, 10, 25), null, null);
        var dueOct26 = new DueSpec(DueKind.Date, new DateOnly(2026, 10, 26), null, null);

        // Input: Create parent P (Assignee=甲, deadline 2026-10-25), child C (mask 63), child D (mask 47, no deadline)
        var createdP = (await service.SaveAsync(new SaveRecordRequest(
            parentId, 0, RecordKind.Task, null, "主任务P", "描述",
            new EffectiveFields("发布人", "甲", Priority.Normal, null, dueOct25, null),
            InheritField.None), CancellationToken.None)).Changed.Single();

        var createdC = (await service.SaveAsync(new SaveRecordRequest(
            childCId, 0, RecordKind.Task, parentId, "子任务C", "",
            new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
            InheritField.All), CancellationToken.None)).Changed.Single();

        var createdD = (await service.SaveAsync(new SaveRecordRequest(
            childDId, 0, RecordKind.Task, parentId, "子任务D", "",
            new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null),
            InheritField.All & ~InheritField.Due), CancellationToken.None)).Changed.Single();

        Assert.Equal("甲", createdC.Effective.Assignee);
        Assert.Equal(dueOct25, createdC.Effective.Due);
        Assert.Equal("甲", createdD.Effective.Assignee);
        Assert.Equal(DueSpec.Empty, createdD.Effective.Due);

        long staleVersionC = createdC.Version;

        // Op 1: Save P Assignee=乙, deadline 2026-10-26
        var op1 = await service.SaveAsync(new SaveRecordRequest(
            parentId, createdP.Version, RecordKind.Task, null, "主任务P", "描述",
            new EffectiveFields("发布人", "乙", Priority.Normal, null, dueOct26, null),
            InheritField.None), CancellationToken.None);

        Assert.Equal(3, op1.Changed.Count);
        var afterOp1C = (await service.GetAsync(childCId, CancellationToken.None))!;
        var afterOp1D = (await service.GetAsync(childDId, CancellationToken.None))!;
        Assert.Equal("乙", afterOp1C.Effective.Assignee);
        Assert.Equal(dueOct26, afterOp1C.Effective.Due);
        Assert.Equal("乙", afterOp1D.Effective.Assignee);
        Assert.Equal(DueSpec.Empty, afterOp1D.Effective.Due);
        Assert.True(afterOp1C.Version > staleVersionC);

        // Op 2: C sets Assignee="乙" independently, clearing Assignee bit; P changes to "丙"
        var afterOp2C = (await service.SaveAsync(new SaveRecordRequest(
            childCId, afterOp1C.Version, RecordKind.Task, parentId, "子任务C", "",
            afterOp1C.Own with { Assignee = "乙" },
            InheritField.All & ~InheritField.Assignee), CancellationToken.None)).Changed.Single();

        var currentP = (await service.GetAsync(parentId, CancellationToken.None))!;
        await service.SaveAsync(new SaveRecordRequest(
            parentId, currentP.Version, RecordKind.Task, null, "主任务P", "描述",
            currentP.Own with { Assignee = "丙" },
            InheritField.None), CancellationToken.None);

        var checkC = (await service.GetAsync(childCId, CancellationToken.None))!;
        var checkD = (await service.GetAsync(childDId, CancellationToken.None))!;
        Assert.Equal("乙", checkC.Effective.Assignee);
        Assert.Equal("丙", checkD.Effective.Assignee);

        // Op 3: C restores Assignee inherit bit -> immediately shows "丙"
        var afterOp3C = (await service.SaveAsync(new SaveRecordRequest(
            childCId, checkC.Version, RecordKind.Task, parentId, "子任务C", "",
            checkC.Own,
            InheritField.All), CancellationToken.None)).Changed.Single();
        Assert.Equal("丙", afterOp3C.Effective.Assignee);

        // Op 4: Save C with staleVersionC -> Conflict, body & projection untouched
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            service.SaveAsync(new SaveRecordRequest(
                childCId, staleVersionC, RecordKind.Task, parentId, "被旧版本篡改", "旧正文",
                checkC.Own, InheritField.All), CancellationToken.None));
        Assert.Equal(ErrorCodes.Conflict, ex.Code);

        var finalC = (await service.GetAsync(childCId, CancellationToken.None))!;
        Assert.Equal("子任务C", finalC.Title);
        Assert.Equal("丙", finalC.Effective.Assignee);
    }

    [Fact]
    public async Task Parent_update_that_breaks_child_start_deadline_order_rolls_back_atomically()
    {
        using var store = new SqliteStore(":memory:");
        var service = new RecordService(store, zoneProvider: () => TestZone);

        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        var dueOct30 = new DueSpec(DueKind.Date, new DateOnly(2026, 10, 30), null, null);
        var p = (await service.SaveAsync(new SaveRecordRequest(
            parentId, 0, RecordKind.Task, null, "P", "",
            new EffectiveFields(null, null, Priority.Normal, null, dueOct30, null),
            InheritField.None), CancellationToken.None)).Changed.Single();

        // Child has independent Start = 2026-10-28, inherits Due from parent
        var childStart = new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.FromHours(8));
        await service.SaveAsync(new SaveRecordRequest(
            childId, 0, RecordKind.Task, parentId, "C", "",
            new EffectiveFields(null, null, Priority.Normal, childStart, DueSpec.Empty, null),
            InheritField.All & ~InheritField.Start), CancellationToken.None);

        // Moving parent Due to 2026-10-20 makes child Start (Oct 28) > Due (Oct 20)
        var dueOct20 = new DueSpec(DueKind.Date, new DateOnly(2026, 10, 20), null, null);
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            service.SaveAsync(new SaveRecordRequest(
                parentId, p.Version, RecordKind.Task, null, "P-Updated", "",
                new EffectiveFields(null, null, Priority.Normal, null, dueOct20, null),
                InheritField.None), CancellationToken.None));

        Assert.Equal(ErrorCodes.Validation, ex.Code);
        Assert.Contains(childId, ex.AffectedIds);

        // Parent remains at original version and deadline
        var pAfter = (await service.GetAsync(parentId, CancellationToken.None))!;
        Assert.Equal(dueOct30, pAfter.Effective.Due);
        Assert.Equal(p.Version, pAfter.Version);
    }
}
