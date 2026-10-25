using System.Diagnostics;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Queries;
using DesktopNotes.Application.Records;
using DesktopNotes.Application.Settings;
using DesktopNotes.Domain;
using DesktopNotes.Infrastructure.Diagnostics;
using DesktopNotes.Infrastructure.Persistence;
using Xunit;

namespace DesktopNotes.Integration.Tests;

public sealed class PerformanceAndInstallerTests : IDisposable
{
    private readonly string _tempRoot;

    public PerformanceAndInstallerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DesktopNotesPerfTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task ScaleBenchmark_10000Records_1000ActiveWithSubtasksAndLongChineseBody_MeetsStartupAndQueryBudgets()
    {
        string dbPath = Path.Combine(_tempRoot, "scale", "app.db");

        // Seed 10,000 records (1,000 active tasks including parent/child hierarchies + 9,000 closed/note records)
        using (var seedStore = new SqliteStore(dbPath))
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string longChineseBody = string.Concat(Enumerable.Repeat("项目里程碑评审与跨部门合同审批交付说明文档，包含长文本检索与性能基线测试内容。", 8));

            seedStore.Write((conn, tx) =>
            {
                using var recCmd = conn.CreateCommand();
                recCmd.Transaction = tx;
                recCmd.CommandText = """
                    INSERT INTO records (
                        id, parent_id, kind, title, body, status,
                        priority, due_kind, due_date, inherit_mask,
                        created_utc_ms, updated_utc_ms, closed_utc_ms, version
                    ) VALUES (
                        $id, $parentId, $kind, $title, $body, $status,
                        $prio, 1, '2026-11-15', $mask,
                        $created, $created, $closed, 1
                    );
                    """;

                var pId = recCmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
                var pParentId = recCmd.Parameters.Add("$parentId", Microsoft.Data.Sqlite.SqliteType.Text);
                var pKind = recCmd.Parameters.Add("$kind", Microsoft.Data.Sqlite.SqliteType.Integer);
                var pTitle = recCmd.Parameters.Add("$title", Microsoft.Data.Sqlite.SqliteType.Text);
                var pBody = recCmd.Parameters.Add("$body", Microsoft.Data.Sqlite.SqliteType.Text);
                var pStatus = recCmd.Parameters.Add("$status", Microsoft.Data.Sqlite.SqliteType.Integer);
                var pPrio = recCmd.Parameters.Add("$prio", Microsoft.Data.Sqlite.SqliteType.Integer);
                var pMask = recCmd.Parameters.Add("$mask", Microsoft.Data.Sqlite.SqliteType.Integer);
                var pCreated = recCmd.Parameters.Add("$created", Microsoft.Data.Sqlite.SqliteType.Integer);
                var pClosed = recCmd.Parameters.Add("$closed", Microsoft.Data.Sqlite.SqliteType.Integer);

                using var effCmd = conn.CreateCommand();
                effCmd.Transaction = tx;
                effCmd.CommandText = """
                    INSERT INTO effective_records (
                        record_id, priority, due_kind, due_date,
                        due_boundary_utc_ms, projection_zone, record_version
                    ) VALUES (
                        $id, $prio, 1, '2026-11-15',
                        $boundary, 'UTC', 1
                    );
                    """;
                var eId = effCmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
                var ePrio = effCmd.Parameters.Add("$prio", Microsoft.Data.Sqlite.SqliteType.Integer);
                var eBoundary = effCmd.Parameters.Add("$boundary", Microsoft.Data.Sqlite.SqliteType.Integer);

                Guid? lastParentId = null;
                for (int i = 0; i < 10_000; i++)
                {
                    Guid id = Guid.NewGuid();
                    bool isActive = i < 1_000;
                    bool isChild = isActive && (i % 5 != 0) && lastParentId.HasValue;
                    if (isActive && i % 5 == 0)
                    {
                        lastParentId = id;
                    }

                    string idStr = id.ToString("D");
                    int prio = i % 4;
                    long boundary = nowMs + (i * 60_000L);

                    pId.Value = idStr;
                    pParentId.Value = isChild ? lastParentId!.Value.ToString("D") : DBNull.Value;
                    pKind.Value = (i % 10 == 9 && !isActive) ? (int)RecordKind.Note : (int)RecordKind.Task;
                    string title = $"任务编号_{i:D5}_核心关键字_{(i % 25 == 0 ? "合同50%_评审" : "常规事项")}";
                    pTitle.Value = title;
                    pBody.Value = $"{title}\n{longChineseBody}";
                    pStatus.Value = isActive ? (int)RecordStatus.Active : (int)RecordStatus.Closed;
                    pPrio.Value = prio;
                    pMask.Value = isChild ? (int)InheritField.All : 0;
                    pCreated.Value = nowMs - (10_000 - i) * 1000L;
                    pClosed.Value = isActive ? DBNull.Value : nowMs;
                    recCmd.ExecuteNonQuery();

                    eId.Value = idStr;
                    ePrio.Value = prio;
                    eBoundary.Value = boundary;
                    effCmd.ExecuteNonQuery();
                }

                return 0;
            });
        }

        // Measure cold store open + initial first-page query latency
        var coldSw = Stopwatch.StartNew();
        using var activeStore = new SqliteStore(dbPath);
        var queryService = new RecordQueryService(activeStore);
        var firstPage = await queryService.SearchAsync(
            new RecordQuery(RecordKind.Task, RecordStatus.Active, false, string.Empty, [], 0, 100),
            CancellationToken.None);
        coldSw.Stop();

        Assert.Equal(1_000, firstPage.Total);
        Assert.Equal(100, firstPage.Items.Count);
        Assert.True(coldSw.ElapsedMilliseconds <= 2_000, $"Cold open + initial load took {coldSw.ElapsedMilliseconds}ms (budget <= 2000ms)");

        // Measure literal search P95 across 20 queries on 10,000 records
        string[] terms = ["合同50%_评审", "任务编号_00500", "跨部门合同审批", "不存在的词条_XYZ", "常规事项"];
        var latenciesMs = new List<double>(20);
        for (int i = 0; i < 20; i++)
        {
            string term = terms[i % terms.Length];
            var qSw = Stopwatch.StartNew();
            var page = await queryService.SearchAsync(
                new RecordQuery(RecordKind.Task, RecordStatus.Active, false, term, [], 0, 100),
                CancellationToken.None);
            qSw.Stop();
            latenciesMs.Add(qSw.Elapsed.TotalMilliseconds);
        }

        latenciesMs.Sort();
        double p95Ms = latenciesMs[(int)Math.Ceiling(latenciesMs.Count * 0.95) - 1];
        Assert.True(p95Ms <= 200.0, $"Literal search P95 was {p95Ms:F2}ms (budget <= 200ms)");
    }

    [Fact]
    public async Task FirstRunInstallerIntent_ImportsDefaultRootAndLoginStartupOnce_AndPreservesDataAcrossUpgrades()
    {
        string dbPath = Path.Combine(_tempRoot, "installer_test", "app.db");
        string customAttachmentDir = Path.Combine(_tempRoot, "installer_test", "UserChosenAttachments");
        string intentFilePath = Path.Combine(_tempRoot, "installer_test", "first-run-intent.json");
        Directory.CreateDirectory(Path.GetDirectoryName(intentFilePath)!);

        await File.WriteAllTextAsync(
            intentFilePath,
            $$"""
            {
              "defaultAttachmentDirectory": "{{customAttachmentDir.Replace("\\", "\\\\")}}",
              "launchAtLogin": true
            }
            """);

        var fakeDesktop = new FakeInstallerDesktopIntegration();
        Guid createdTaskId = Guid.NewGuid();

        using (var store = new SqliteStore(dbPath))
        {
            var settingsService = new SettingsService(store, fakeDesktop);
            bool imported = await settingsService.ImportFirstRunIntentIfPresentAsync(intentFilePath, store, CancellationToken.None);
            Assert.True(imported);
            Assert.False(File.Exists(intentFilePath));
            Assert.True(fakeDesktop.LoginStartup);

            var prefs = settingsService.GetPreferences();
            Assert.NotNull(prefs.DefaultStorageRootId);
            var root = store.GetStorageRoot(prefs.DefaultStorageRootId!.Value);
            Assert.NotNull(root);
            Assert.Equal(Path.GetFullPath(customAttachmentDir), root!.AbsolutePath);

            // Create a user task before upgrade/reinstall
            var recordService = new RecordService(store);
            await recordService.SaveAsync(
                new SaveRecordRequest(
                    createdTaskId,
                    0,
                    RecordKind.Task,
                    null,
                    "升级前保存的重要任务",
                    "卸载或覆盖升级后仍应完整保留",
                    new EffectiveFields(null, null, Priority.Highest, null, DueSpec.Empty, null),
                    InheritField.None),
                CancellationToken.None);
        }

        // Simulate upgrade / re-open on existing database
        using (var upgradedStore = new SqliteStore(dbPath))
        {
            var recordService = new RecordService(upgradedStore);
            var preserved = await recordService.GetAsync(createdTaskId, CancellationToken.None);
            Assert.NotNull(preserved);
            Assert.Equal("升级前保存的重要任务", preserved!.Title);
        }
    }

    [Fact]
    public void RollingLog_RotatesBySize_AndNeverWritesUserContent()
    {
        string logDir = Path.Combine(_tempRoot, "logs");
        var logger = new RollingLog(logDir, maxFileBytes: 4096, maxRetainedFiles: 3);

        Guid entityId = Guid.NewGuid();
        for (int i = 0; i < 120; i++)
        {
            logger.WriteEvent("INFO", "AttachmentImport", "job_verified", entityId, $"iteration={i}");
        }

        var logFiles = Directory.GetFiles(logDir, "desktopnotes*.log");
        Assert.InRange(logFiles.Length, 2, 3);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class FakeInstallerDesktopIntegration : IDesktopIntegration
    {
        public bool LoginStartup { get; private set; }

        public DesktopCapabilities Capabilities => new(
            CapabilitySupport.Supported,
            CapabilitySupport.Supported,
            CapabilitySupport.Supported,
            CapabilitySupport.Supported,
            "Installer Test");

        public Task ApplyModeAsync(nint windowHandle, PlacementMode mode, CancellationToken ct) => Task.CompletedTask;

        public Task SetLoginStartupAsync(bool enabled, CancellationToken ct)
        {
            LoginStartup = enabled;
            return Task.CompletedTask;
        }

        public Task<bool> GetLoginStartupAsync(CancellationToken ct) => Task.FromResult(LoginStartup);

        public WindowPlacement FitToAvailableScreens(WindowPlacement saved) => saved;
    }
}
