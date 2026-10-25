using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Queries;
using DesktopNotes.Application.Records;
using DesktopNotes.Domain;
using DesktopNotes.Infrastructure.Persistence;
using Xunit;

namespace DesktopNotes.Integration.Tests;

public class QueryTests
{
    private static readonly TimeZoneInfo TestZone = TimeZoneInfo.CreateCustomTimeZone(
        "TestUTC8", TimeSpan.FromHours(8), "TestUTC8", "TestUTC8");

    [Fact]
    public async Task Literal_search_matches_exact_substrings_and_ignores_sql_wildcards()
    {
        using var store = new SqliteStore(":memory:");
        var service = new RecordService(store, zoneProvider: () => TestZone);
        var queryService = new RecordQueryService(store, () => TestZone);

        var samples = new (Guid Id, string Body)[]
        {
            (Guid.Parse("00000000-0000-0000-0000-000000000001"), "合同审批"),
            (Guid.Parse("00000000-0000-0000-0000-000000000002"), "合 同"),
            (Guid.Parse("00000000-0000-0000-0000-000000000003"), "协议"),
            (Guid.Parse("00000000-0000-0000-0000-000000000004"), "50%_O'Reilly"),
            (Guid.Parse("00000000-0000-0000-0000-000000000005"), "Contract"),
            (Guid.Parse("00000000-0000-0000-0000-000000000006"), new string('前', 40) + "合同")
        };

        var emptyOwn = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);
        foreach (var (id, body) in samples)
        {
            await service.SaveAsync(new SaveRecordRequest(
                id, 0, RecordKind.Task, null, "", body, emptyOwn, InheritField.None), CancellationToken.None);
        }

        async Task<Guid[]> SearchIds(string text)
        {
            var page = await queryService.SearchAsync(new RecordQuery(
                RecordKind.Task, RecordStatus.Active, false, text, Array.Empty<SortTerm>(), 0, 50), CancellationToken.None);
            return page.Items.Select(x => x.Id).ToArray();
        }

        Assert.Equal(new[] { samples[0].Id, samples[5].Id }, await SearchIds("合同"));
        Assert.Equal(new[] { samples[3].Id }, await SearchIds("%_"));
        Assert.Equal(new[] { samples[3].Id }, await SearchIds("O'Reilly"));
        Assert.Empty(await SearchIds("contract"));
        Assert.Equal(6, (await SearchIds("")).Length);
    }

    [Fact]
    public async Task Default_and_custom_sorting_keep_null_deadlines_last_and_filter_scopes()
    {
        using var store = new SqliteStore(":memory:");
        var service = new RecordService(store, zoneProvider: () => TestZone);
        var queryService = new RecordQueryService(store, () => TestZone);

        var idA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        var idB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
        var idC = Guid.Parse("00000000-0000-0000-0000-00000000000c");
        var idD = Guid.Parse("00000000-0000-0000-0000-00000000000d");

        DueSpec InstantDue(long ms) => new(DueKind.Instant, null, DateTimeOffset.FromUnixTimeMilliseconds(ms), "Asia/Shanghai");

        await service.SaveAsync(new SaveRecordRequest(
            idA, 0, RecordKind.Task, null, "A", "",
            new EffectiveFields(null, null, Priority.Highest, null, DueSpec.Empty, null), InheritField.None), CancellationToken.None);

        await service.SaveAsync(new SaveRecordRequest(
            idB, 0, RecordKind.Task, null, "B", "",
            new EffectiveFields(null, null, Priority.High, null, InstantDue(100_000), null), InheritField.None), CancellationToken.None);

        await service.SaveAsync(new SaveRecordRequest(
            idC, 0, RecordKind.Task, null, "C", "",
            new EffectiveFields(null, null, Priority.Highest, null, InstantDue(200_000), null), InheritField.None), CancellationToken.None);

        await service.SaveAsync(new SaveRecordRequest(
            idD, 0, RecordKind.Task, null, "D", "",
            new EffectiveFields(null, null, Priority.Highest, null, InstantDue(200_000), null), InheritField.None), CancellationToken.None);

        var defaultPage = await queryService.SearchAsync(new RecordQuery(
            RecordKind.Task, RecordStatus.Active, false, "", Array.Empty<SortTerm>(), 0, 10), CancellationToken.None);

        Assert.Equal(4, defaultPage.Total);
        Assert.Equal(new[] { idC, idD, idA, idB }, defaultPage.Items.Select(x => x.Id).ToArray());

        // Sort by Deadline Descending: items with deadlines come first (200_000 then 100_000), null deadline idA stays at the end!
        var descDeadlinePage = await queryService.SearchAsync(new RecordQuery(
            RecordKind.Task, RecordStatus.Active, false, "",
            new[] { new SortTerm(SortKey.Deadline, Descending: true) }, 0, 10), CancellationToken.None);

        Assert.Equal(idA, descDeadlinePage.Items.Last().Id);

        // Pagination check
        var paged = await queryService.SearchAsync(new RecordQuery(
            RecordKind.Task, RecordStatus.Active, false, "", Array.Empty<SortTerm>(), 1, 2), CancellationToken.None);
        Assert.Equal(4, paged.Total);
        Assert.Equal(new[] { idD, idA }, paged.Items.Select(x => x.Id).ToArray());
    }
}
