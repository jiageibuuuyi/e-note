# DesktopNotes V1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

本文交付给自行开发的工程师，按勾选项顺序实施即可。上面的 Agent 执行提示只适用于具备相应技能的自动化环境；这些技能不属于应用依赖，也不是人工开发的前置条件。本次只交付文档与数据设计参考，不启动应用开发。

**Goal:** 实现可长期常驻的本地任务/笔记工具，交付 Windows V1，并建立 macOS/Linux 共用代码与平台适配路径。

**Architecture:** Avalonia 共享界面；Domain 纯规则；Application 统一业务入口；Infrastructure 管理 SQLite 和文件；Platforms 处理系统差异。父子任务有效字段在写入事务中同步投影，附件作业通过持久化状态机恢复。

**Tech Stack:** .NET 10 LTS、Avalonia 12.1.4、Microsoft.Data.Sqlite 10.0.13、xUnit；Python 仅验证参考 SQL。

**Spec:** [开发实施指南](../../开发实施指南.md)、[产品计划书](../../../桌面待办与笔记应用_项目计划书.md)、[架构记录](../../adr/0002-cross-platform-desktop-architecture.md)。

## Global Constraints

- “第一版支持 Windows 10 / Windows 11，x64 起步；macOS、Linux 是已确定的后续交付目标”。
- “按原文连续包含匹配”，不做模糊联想；查询输入不作 SQL 通配符。
- “未单独修改的继承字段跟随主任务变化，已单独修改的字段保持独立”。
- “拖入始终视为移动”，复制粘贴保留源项，剪切粘贴移动；V1 支持整个文件夹。
- 个人本地使用，任务/笔记双入口，多行纯文本，四档优先级，标题或内容至少一项。
- 系统通知默认关闭；数据与安装目录分离；卸载默认保留数据。
- 不以普通窗口置底替代真正桌面嵌入；未验证平台能力不得标记已支持。
- 参考 SQL 的继承掩码和有效值投影是统一模型，不另建重复的覆盖写入链路。

## Review Focus

1. 导入时源目录新增文件或文件被替换：不能在清理阶段误删，归 T07。
2. 子任务显式清空截止日期：必须停止继承且保持空值，归 T02/T04。
3. 查询输入包含 `%_`、引号或中文内部空格：必须按字面匹配，归 T03/T05。
4. 文件已发布但数据库未登记时强退：必须找回同一附件且不重复清源，归 T07。
5. 恢复别的电脑备份：不得重放源设备的删除作业，归 T10。

每个任务开始前读取指南的对应章节。任务内先写针对行为的测试或按给定步骤建立失败基线，再实现，再验证；不要为常量和自动生成模板写镜像测试。一次提交包含该任务实现、必要测试及记录。所有命令从项目根目录执行。

## T01：建立工程与平台风险原型

**Files:** 创建指南第3节中的五个源项目与两个测试项目；创建 `docs/verification/platform-prototype.md`、`src/DesktopNotes.Platforms/Windows/DesktopHostProbe.cs`、`src/DesktopNotes.Desktop/Views/PlatformProbeWindow.axaml`。

**Interfaces:** 消费指南第9节 `IDesktopIntegration`；产出 Windows 原型实现与每项 capability 的证据。样例数据只在内存，不访问真实用户文件。

- [ ] 按指南第2节创建工程、统一 net10.0、锁定包；首次窗口使用下列探针内容，确保普通窗口先可操作。

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        x:Class="DesktopNotes.Desktop.Views.PlatformProbeWindow"
        Width="350" Height="520" Title="平台验证">
  <StackPanel Margin="16" Spacing="12">
    <TextBlock Text="桌面嵌入 / 输入 / 拖放探针"/>
    <TextBox Watermark="输入中文并切换窗口" AcceptsReturn="True" Height="120"/>
    <Border Name="DropTarget" Background="#EEEEEE" Padding="20">
      <TextBlock Text="将临时测试文件拖到这里"/>
    </Border>
    <TextBlock Name="ProbeStatus" Text="普通窗口基线"/>
  </StackPanel>
</Window>
```

- [ ] 创建对应 code-behind 并调用模板的 InitializeComponent；为 DropTarget 接入 Avalonia 文件拖放事件，仅记录测试文件名称，不移动。使用所锁定版本的文件 DataTransfer API，系统剪切标记在平台层读取。
- [ ] 在普通窗口验证中文输入、双击、拖放；再实现 ApplyModeAsync，验证置顶、桌面连接/解除连接及恢复。不要把宿主连接逻辑写在任务 ViewModel。
- [ ] 逐项执行：Win+D两次、显示桌面按钮、切换普通应用、切换缩放、双屏移动、Explorer重启、睡眠恢复；记录预期“仍可见/能输入/能拖入”与实际结果。
- [ ] 在可用 macOS/Linux 环境打开同一探针，记录具体系统/桌面/会话；缺环境标“未验证”。Linux窗口定位受限时记录受影响功能及候选方案，不能假装通过。
- [ ] 执行 `dotnet build -c Release`、`dotnet test -c Release`，提交 `chore: establish cross-platform shell and capability probes`。此阶段测试项目可暂时没有业务用例，不能用模板空测试宣称业务已验证。

**交付门：** Windows真正桌面模式的输入及外部拖放通过，或明确停留在原型攻关；不允许把此问题掩盖到发布前。

## T02：实现名称、搜索、字段继承与日期规则

**Files:** 创建指南第3节 Domain 文件；测试 `tests/DesktopNotes.Domain.Tests/RecordRulesTests.cs`、`DeadlineRulesTests.cs`。

**Interfaces:** 产出指南第4节全部类型、`InheritanceResolver.Resolve`、`DisplayName.Create`、`LiteralSearch.Matches`。日期方法契约：`DateTimeOffset? DeadlineRules.Boundary(DueSpec due, TimeZoneInfo displayZone)`；无截止返回 null，日期边界按指南第7节计算。

- [ ] 先加入以下业务测试，运行 `dotnet test --filter FullyQualifiedName~RecordRulesTests`，首次应因类型尚未实现而失败。

```csharp
using DesktopNotes.Domain;
using Xunit;
namespace DesktopNotes.Domain.Tests;

public class RecordRulesTests
{
    [Fact]
    public void Generated_name_does_not_split_an_emoji()
    {
        var prefix = new string('字', 29) + "😀";
        Assert.Equal(prefix + "…", DisplayName.Create(" ", prefix + "尾"));
    }
    [Theory]
    [InlineData("合同", "合同审批", true)]
    [InlineData("合同", "合 同", false)]
    [InlineData("%_", "预算50%_说明", true)]
    [InlineData("contract", "Contract", false)]
    public void Query_is_literal(string query, string body, bool expected)
        => Assert.Equal(expected, LiteralSearch.Matches(query, body));

    [Fact]
    public void Explicit_empty_due_does_not_inherit()
    {
        var none = new EffectiveFields(null, null, Priority.Normal,
            null, DueSpec.Empty, null);
        var parent = none with { Due = new(DueKind.Date,
            new DateOnly(2026, 10, 25), null, null), Assignee = "甲" };
        var result = InheritanceResolver.Resolve(none, parent,
            InheritField.All & ~InheritField.Due);
        Assert.Equal(DueSpec.Empty, result.Due);
        Assert.Equal("甲", result.Assignee);
    }
}
```

- [ ] 使用指南第4节代码实现名称、字面匹配及继承；加入独立同值、恢复继承测试。日期实现使用时间区转换，拒绝不存在/歧义的编辑时刻，不固定加24小时计算日期边界。
- [ ] 增加下列日期测试，执行 `dotnet test --filter FullyQualifiedName~DeadlineRulesTests`；期望 PASS。

```csharp
using DesktopNotes.Domain;
using Xunit;
namespace DesktopNotes.Domain.Tests;
public class DeadlineRulesTests
{
    [Fact]
    public void Date_only_expires_at_next_local_day()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("TestUTC8",
            TimeSpan.FromHours(8), "TestUTC8", "TestUTC8");
        var due = new DueSpec(DueKind.Date, new DateOnly(2026,10,25), null, null);
        Assert.Equal(new DateTimeOffset(2026,10,25,16,0,0,TimeSpan.Zero),
            DeadlineRules.Boundary(due, zone));
        Assert.Null(DeadlineRules.Boundary(DueSpec.Empty, zone));
    }
}
```

- [ ] 补充真实夏令时时区的跳时/回拨日期测试，记录运行平台时区ID；运行全部 Domain.Tests，提交 `feat: define record rules and inherited values`。

## T03：SQLite、迁移、搜索与单写事务

**Files:** 创建 `Persistence/SqliteStore.cs`、`MigrationRunner.cs`、`Migrations/001_initial.sql`（复制配套SQL）、`Queries/RecordQueryService.cs`；测试 `SqliteSchemaTests.cs`、`QueryTests.cs`。

**Interfaces:** 消费指南第5节 schema 和第6节查询 DTO；产出 `IRecordQueryService.SearchAsync`。SqliteStore 提供 `T Read<T>(Func<SqliteConnection,T> action)` 与 `T Write<T>(Func<SqliteConnection,SqliteTransaction,T> action)`；方法仅在受控后台执行，Write 开始事务、成功提交、异常回滚，connection/transaction 不逃逸到 UI。

- [ ] 执行 `python docs/database/verify_schema.py`，确认参考模型的14项检查。它不代替 C# 驱动集成测试。
- [ ] 为 Integration.Tests 添加 `Microsoft.Data.Sqlite` 显式包引用并锁定10.0.13；工程中配置参考 SQL 复制到输出目录。

```xml
<ItemGroup>
  <None Include="../../docs/database/001_initial.sql"
        Link="001_initial.sql" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] 添加以下测试并运行 `dotnet test --filter FullyQualifiedName~SqliteSchemaTests`，实现迁移前先验证SQL路径和约束能被实际驱动读取。

```csharp
using Microsoft.Data.Sqlite;
using Xunit;
namespace DesktopNotes.Integration.Tests;
public class SqliteSchemaTests
{
    [Fact]
    public void Schema_initializes_and_literal_symbols_are_not_wildcards()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "001_initial.sql"));
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", command.ExecuteScalar());
        command.CommandText = "SELECT instr('50%_合同',$q)";
        command.Parameters.AddWithValue("$q", "%_");
        Assert.Equal(3L, command.ExecuteScalar());
    }
}
```

- [ ] 实现本机数据目录、WAL、外键和MigrationRunner；启动重复执行只读取已执行版本，不再次运行同名CREATE TABLE。
- [ ] 实现指南第5.3节SQL；查询边界 Skip≥0、Take在1–200；参数化，ORDER BY仅枚举白名单；输入trim在应用层完成。
- [ ] 将 verify_schema 中搜索/排序数据集移植到 QueryTests；另测 active/closed/trash范围、分页总数、无deadline降序仍在末尾。运行全部集成测试，提交 `feat: persist records and add literal queries`。

## T04：记录写入、动态继承与归档生命周期

**Files:** 创建 `RecordService.cs`、`ProjectionBuilder.cs`、`Contracts/RecordContracts.cs`；测试 `RecordServiceTests.cs`、`InheritanceTransactionTests.cs`。

**Interfaces:** 消费 SqliteStore、Domain类型；实现指南第6节完整 `IRecordService`，事务内维护records、effective_records、completion_snapshots。

- [ ] 先写集成行为用例，按下面固定数据和结果建立测试；通过测试夹具创建临时数据库和真实RecordService，禁止使用“永远返回成功”的仓储替身。

```text
输入：创建父P(责任人甲、deadline 2026-10-25)，子C(掩码63)，子D(掩码47、无deadline)
操作1：保存P责任人乙、deadline 2026-10-26
断言：C显示乙/10-26；D显示乙/无deadline；三条投影和版本在同一提交中更新
操作2：C将责任人单独设为乙，清除Assignee位；父改丙
断言：C仍乙，D变丙
操作3：C恢复Assignee继承位
断言：C立即显示丙
操作4：用操作1之前的C版本保存
断言：Conflict，正文和投影均未被旧值覆盖
```

- [ ] 实现指南第5.2节事务流程，使用上面的Resolve函数；校验所有受影响活动子任务后一起提交，任意日期冲突整笔回滚。
- [ ] 按指南第6.1节实现Close/Reopen/Trash/Restore；测试批量完成子先父后，重开父先子后，删除批次不恢复此前单独删除的子项。
- [ ] 测试完成子任务后父deadline变化：归档子任务仍显示旧deadline；重开后按继承配置更新。数据库直接SQL改父不会自动传播，因此对所有业务入口做测试，禁止UI绕过服务。
- [ ] 运行 `dotnet test --filter FullyQualifiedName~InheritanceTransactionTests` 和 `dotnet test --filter FullyQualifiedName~RecordServiceTests`，提交 `feat: implement task lifecycle and live inheritance`。

## T05：任务/笔记面板、详情、精准搜索

**Files:** 创建 `PanelWindow.axaml`、`DetailWindow.axaml`、`TaskRow.axaml`、对应ViewModels、`TaskRowInteraction.cs`；测试 `docs/verification/ui-core.md`。

**Interfaces:** 仅调用IRecordService/IRecordQueryService；产出可保存、重启、编辑和查询的核心应用。草稿通过应用层写drafts，UI不持有数据库连接。

- [ ] 用下列查询构造默认任务页，分页加载并虚拟化行；笔记页把kind改为Note。

```csharp
var query = new DesktopNotes.Application.Contracts.RecordQuery(
    DesktopNotes.Domain.RecordKind.Task,
    DesktopNotes.Domain.RecordStatus.Active,
    false, "",
    new[] {
        new DesktopNotes.Application.Contracts.SortTerm(
            DesktopNotes.Application.Contracts.SortKey.Priority, false),
        new DesktopNotes.Application.Contracts.SortTerm(
            DesktopNotes.Application.Contracts.SortKey.Deadline, false),
        new DesktopNotes.Application.Contracts.SortTerm(
            DesktopNotes.Application.Contracts.SortKey.Created, false)
    }, 0, 100);
```

- [ ] 按指南第10节实现单击、双击、完成框独立事件；用实际系统双击阈值，不写死150ms；在已展开行双击不得先折叠再打开。
- [ ] 实现详情Own/Effective/继承标记、恢复继承、草稿保存、版本冲突提示；用户清空deadline必须清除Due继承位。
- [ ] 搜索200ms防抖，带序号取消旧结果；手工连续输入“合→合同→清空”，最终必须回到原筛选全量结果。
- [ ] 验证表：1000个未完成任务能滚动到底；无标题长正文名称正确；子任务独立可见；搜索“%_”和正文第40字后内容可命中；切换笔记不混入任务；完成后归档、重启仍一致。
- [ ] 保存UI报告（系统、缩放、步骤、结果），运行核心测试后提交 `feat: build desktop panel and record editor`。

## T06：先建立可恢复的文件/文件夹复制基础

**Files:** 创建 `AttachmentService.cs`、`ImportWorker.cs`、`ManagedFileStore.cs`、附件契约；测试 `AttachmentCopyTests.cs`。

**Interfaces:** 实现IAttachmentService.EnqueueAsync/GetJobAsync；先支持Copy和注册，Move暂不开放给用户。遵循指南第8节planned→copying→verified→published→registered→done。

- [ ] 建立测试目录fixture：测试开始在系统临时目录内创建独立UUID目录，结束只清理该绝对目录；路径校验不通过时禁止递归清理。
- [ ] 用以下Python命令创建单次手工测试输入（不删除任何现有内容），传输目标选另一个新临时目录。

```powershell
@'
from pathlib import Path
from tempfile import mkdtemp
root=Path(mkdtemp(prefix="desktopnotes-fixture-"))
(root/"source"/"empty").mkdir(parents=True)
(root/"source"/"nested").mkdir()
(root/"source"/"nested"/"contract.txt").write_text("合同内容",encoding="utf-8")
(root/"source"/"other.txt").write_bytes(b"abc"*1000)
print(root)
'@ | python -
```

- [ ] 实现规划清单、规范路径校验、去除父子重复源、1MiB缓冲复制、SHA-256二次读取验证、发布与登记；空目录单独记录。所有耗时I/O在后台，不跨文件传输持有数据库事务。
- [ ] 集成断言：嵌套文件内容相同、空目录存在、源完整、附件只有一条；两个同名文件生成不同attachment_id；重试同job不重复创建。
- [ ] 故障测试：目标只读、空间不足（可注入文件存储失败）、复制中取消；正式记录未创建且源完整。拒绝链接、自我导入、已管理路径。
- [ ] 执行 `dotnet test --filter FullyQualifiedName~AttachmentCopyTests`，提交 `feat: add verified attachment transfer pipeline`。

## T07：移动、恢复、拖入和剪切粘贴

**Files:** 创建 `RecoveryService.cs`、平台文件身份与剪贴板适配；测试 `AttachmentMoveTests.cs`、`AttachmentRecoveryTests.cs`、`docs/verification/clipboard.md`。

**Interfaces:** 在T06基础实现Move、Cancel/Retry以及IFileClipboard；正式拖放入口固定Move。清源平台端的内部方法契约为 `RemoveVerifiedSource(sourcePath, expectedIdentity, expectedSha256)`，返回Removed/Changed/Denied/Uncertain之一，不抛出后吞掉源丢失信息。

- [ ] 先实现无删除的恢复对账，给ImportWorker每个持久化阶段加入测试故障点，测试进程可在阶段完成后退出；生产关闭这些故障开关。
- [ ] 按指南第8.3节实现registered后清源；只有全部目标校验完成才能清理目录源。使用平台身份/句柄约束，无法可靠删除则source_cleanup_pending。
- [ ] 执行以下故障矩阵并在集成测试中断言两端内容和job状态。

```text
故障A：verified后强退          → 重启继续发布；源未提前删除
故障B：published后、登记前强退 → 找回目标并登记同attachment_id
故障C：registered后删源被拒绝  → 正式附件可打开，源仍在，显示cleanup_pending
故障D：目录复制期间新增文件X   → X保留，不递归删除源树，任务报告差异
故障E：清理前源路径被替换      → SourceChanged/needs_review，替换文件不删除
故障F：清理一部分后取消        → 目标保留，显示未完成；不声称整体回滚
```

- [ ] 接入文件拖入和粘贴：Explorer复制=Copy，剪切=Move，拖入=Move；修饰键不改变产品语义；未知剪切标记=Copy；文本框内文本正常粘贴。验证系统协议不会再清源一次。
- [ ] 拖入期间再复制其他文本，完成后剪贴板保持新文本；重复粘贴同一失效剪切项返回源不存在，不移动无关文件。
- [ ] 同盘及真实跨盘至少各验证一次；运行 `dotnet test --filter FullyQualifiedName~Attachment`，提交 `feat: support recoverable moves and clipboard attachments`。

**交付门：** 只有此任务通过，才允许对真实用户文件开放默认移动。T06复制基础不算R10完成。

## T08：到期提醒、月历、归档查询

**Files:** 创建 `ReminderScheduler.cs`、`DeadlineRules.cs`剩余逻辑、`CalendarViewModel.cs`及月历视图；测试 `ReminderTests.cs`、`CalendarQueryTests.cs`。

**Interfaces:** IRecordQueryService.CalendarAsync；注入TimeProvider以控制当前时间，不依赖测试运行时的真实日期；平台通知接收标题/消息/记录ID，点击通知回到对应详情。

- [ ] 用TimeProvider测试固定时刻：2026-10-25 23:59:59 +08尚未逾期，26日00:00日期型逾期；精确时间恰好到达即逾期；关闭任务停止提醒。
- [ ] 实现单调度器、下一边界、60秒兜底、恢复/时区变化重算；任务变更后不继续触发旧deadline。
- [ ] 按指南第7.2节先claim后投递，重启claimed不逐条重发；系统通知默认关闭。测试修改正文不改变due_fingerprint，父deadline变化能影响未覆盖子项。
- [ ] 月历实现Due/Start/Created/Closed四维，按本地月半开区间；测试跨UTC日、无日期、完成快照、子任务归属、闰年2月。
- [ ] 运行 `dotnet test --filter FullyQualifiedName~ReminderTests` 与 `dotnet test --filter FullyQualifiedName~CalendarQueryTests`，提交 `feat: add calendar and deadline reminders`。

## T09：正式窗口模式、设置、托盘和登录启动

**Files:** 完成Platforms内Windows适配、`SettingsService.cs`、设置视图；新增 `docs/verification/windows-shell.md`。

**Interfaces:** IDesktopIntegration完整实现；设置JSON按指南第1/9/10节读取默认值；窗口位置与业务记录独立存储。

- [ ] 把T01探针验证通过的宿主逻辑接入正式面板；模式转换异常恢复上一状态，能力报告保留失败原因。
- [ ] 完成托盘新建/显示/日历/退出；关闭按钮隐藏；无托盘时保留任务栏入口。设置登录启动默认关闭，读取系统状态而非只读本地开关。
- [ ] 加入位置/尺寸/监视器/缩放保存、边缘吸附、位置锁定、快捷键冲突提示、明暗主题及字号；位置保存500ms防抖。
- [ ] 按顺序验证：锁定位置后仍能点击；拔掉副屏能找回；125%→200%命中不偏移；Explorer重启后托盘恢复；系统手动禁用启动项后应用不擅自开启；重复启动只出现一个实例。
- [ ] 用键盘Tab/Enter/Esc完成一次新建、搜索、展开、编辑、完成；高对比模式仍能识别到期状态。保存报告，提交 `feat: finalize Windows desktop integration and settings`。

## T10：完整备份、恢复与数据迁移

**Files:** 创建 `BackupService.cs`、`ArchiveWriter.cs`、恢复流程视图；测试 `BackupRestoreTests.cs`、`RestorePathTests.cs`。

**Interfaces:** BackupService公开 `Task ExportAsync(string archivePath, bool includeTrash, CancellationToken ct)`、`Task RestoreAsync(string archivePath, IReadOnlyDictionary<Guid,string> rootMappings, CancellationToken ct)`；错误使用统一AppException。只在用户明确选择恢复后修改当前库。

- [ ] 实现数据库在线备份与7份轮转；完整导出按指南第11节协调作业、快照、文件清单与校验，未完成包使用临时名称。
- [ ] 建立测试数据：父子任务、独立空deadline、完成快照、两个附件根、嵌套及空文件夹、逻辑回收附件；导出再恢复到全新临时目录。
- [ ] 断言记录/继承/状态一致，所有文件哈希和空目录一致；新根路径生效，旧路径不被访问。
- [ ] 导入包含`../outside.txt`或绝对路径的包必须拒绝；大小写冲突必须生成明确映射或拒绝，不覆盖。
- [ ] 包中放入source_cleanup_pending的旧设备job，恢复后不得调用清源端。将清源端设为一调用便使测试失败的spy，验证零次调用。
- [ ] 在附件发布、库切换两个阶段注入失败，重启读取恢复日志，旧库或新库至少一套可恢复；不能把多根目录操作误当数据库事务。
- [ ] 运行 `dotnet test --filter FullyQualifiedName~BackupRestoreTests` 与 `dotnet test --filter FullyQualifiedName~RestorePathTests`，提交 `feat: add complete backup and recoverable restore`。

## T11：安装、升级与Windows V1验收

**Files:** 创建 `installer/windows/DesktopNotes.iss`、`docs/verification/windows-v1.md`、`docs/verification/performance.md`，完成日志轮转与迁移报告。

**Interfaces:** 安装器首启配置含默认附件目录/登录启动意图；SettingsService验证后导入。安装器不直接编辑任务数据库。

- [ ] 执行指南第11节发布命令，打包完整输出；安装器允许选择默认附件目录和登录启动，界面说明程序目录与数据目录不同。
- [ ] 干净机器普通用户安装，新建任务和附件；升级安装后数据完整；卸载默认数据和附件仍在；重装可找回。
- [ ] 生成1万条记录，其中1000未完成，含子任务及长中文正文；测Release冷启动、查询P95、内存、5分钟空闲CPU和8小时趋势。单独测试1GiB文件与1万小文件目录，不把导入峰值当空闲基线。
- [ ] 使用指南第12节矩阵逐项记录结果；未通过项记录复现步骤和受影响版本，修复后只重跑相关项及必要回归。
- [ ] 运行 `dotnet restore --locked-mode`、`dotnet build -c Release`、`dotnet test -c Release`，提交 `release: validate Windows v1 installation and performance`。

**交付门：** 必需功能、附件故障恢复、完整恢复和支持矩阵通过；依赖/平台风险未验证不能写“完整支持”。

## T12：macOS与Linux正式发布

**Files:** 完成Platforms/MacOS、Platforms/Linux；创建 `installer/macos/`、`installer/linux/`、`docs/verification/macos-release.md`、`linux-release.md`。

**Interfaces:** 复用已有应用接口和schema，只实现平台适配；业务规则不复制成第二套系统专用代码。

- [ ] 先固定macOS版本/架构和Linux发行版/桌面/会话组合，按T01证据补齐能力表。
- [ ] 在真实目标环境运行任务、继承、搜索、文件夹拖入、copy/cut粘贴、通知、托盘/菜单栏、启动项和备份迁移；对Wayland窗口能力限制记录可接受替代，不自行宣称等价。
- [ ] 分别生成发行包；macOS处理签名/公证与权限测试，Linux处理所选包格式、桌面入口和依赖。
- [ ] 从Windows完整备份迁移到两端，再导回Windows，验证大小写/Unicode/非法字符映射和无旧设备清源。
- [ ] 两端分别记录性能和系统集成结果，提交对应发布变更。只有通过各自验收的目标版本可以列入支持矩阵。

## 需求覆盖索引

| 需求 | 实现任务 | 验收位置 |
|---|---|---|
| R01/R14 窗口、系统、跨平台 | T01/T09/T11/T12 | 平台与发行报告 |
| R02/R03/R04 字段与任务笔记 | T02/T03/T04/T05 | Domain/RecordService测试 |
| R05/R06/R07 列表排序交互 | T02/T03/T05 | QueryTests与UI报告 |
| R08 子任务继承 | T02/T04/T05/T08 | InheritanceTransactionTests |
| R09 精准搜索 | T02/T03/T05 | SQL脚本、QueryTests、UI报告 |
| R10/R11 附件目录、移动粘贴 | T06/T07/T09/T10 | 附件与恢复测试 |
| R12/R13 提醒、日历、历史 | T04/T08 | Reminder/Calendar测试 |
| 数据可靠性与轻量化 | T03/T07/T10/T11 | 故障恢复与性能报告 |

## 文档与示例的验证状态

- 已验证：配套初始SQL在Python内置SQLite执行，14项自动检查通过；文档内部链接与代码块结构检查。
- 尚未验证：C#代码编译、Avalonia模板创建、系统宿主、剪贴板移动、通知及安装器。当前环境未发现dotnet命令。
- 本计划中的C#测试是交给开发者在相应实现落地后运行的起始样例，不能把存在测试文本当作测试已通过。
