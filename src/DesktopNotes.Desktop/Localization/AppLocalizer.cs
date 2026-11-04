using System.ComponentModel;
using System.Text.Json;
using DesktopNotes.Application.Ports;

namespace DesktopNotes.Desktop.Localization;

public enum AppLanguage
{
    English = 0,
    Chinese = 1
}

public enum AppVisualStyle
{
    Realistic = 0,
    Minimalist = 1
}

public sealed class BuildEnvironmentInfo
{
    public string? DetectedTimeZoneId { get; set; }
    public string? DetectedTimeZoneDisplayName { get; set; }
    public string? DetectedUtcOffset { get; set; }
    public string? DefaultLanguage { get; set; }
}

public sealed class AppLocalizer : INotifyPropertyChanged
{
    public const string LanguageSettingKey = "global.language";
    public const string TimeZoneSettingKey = "global.detected_timezone";
    public const string VisualStyleSettingKey = "global.visual_style";

    public static AppLocalizer Instance { get; } = new();

    private AppLanguage _language = AppLanguage.English;
    private AppVisualStyle _visualStyle = AppVisualStyle.Minimalist;
    private TimeZoneInfo _activeTimeZone = TimeZoneInfo.Local;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;
    public event Action? VisualStyleChanged;

    public AppLanguage Language => _language;
    public bool IsEnglish => _language == AppLanguage.English;
    public bool IsChinese => _language == AppLanguage.Chinese;
    public string LanguageCode => IsChinese ? "zh" : "en";

    public AppVisualStyle VisualStyle => _visualStyle;
    public bool IsRealisticStyle => _visualStyle == AppVisualStyle.Realistic;
    public bool IsMinimalistStyle => _visualStyle == AppVisualStyle.Minimalist;
    public string VisualStyleCode => IsMinimalistStyle ? "minimalist" : "realistic";

    public TimeZoneInfo ActiveTimeZone => _activeTimeZone;

    public string T(string en, string zh) => IsChinese ? zh : en;

    public void InitializeFromEnvironmentAndStore(ISettingsRepositoryPort? store, string? baseDirectory = null)
    {
        // 1. Check build/install time-zone inspection file if present, combined with local system timezone
        TimeZoneInfo resolvedZone = TimeZoneInfo.Local;
        string defaultLangCode = "en";

        try
        {
            string dir = baseDirectory ?? AppContext.BaseDirectory;
            string buildEnvPath = Path.Combine(dir, "build-env.json");
            if (File.Exists(buildEnvPath))
            {
                string rawBuildEnv = File.ReadAllText(buildEnvPath);
                var envInfo = JsonSerializer.Deserialize<BuildEnvironmentInfo>(
                    rawBuildEnv,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (!string.IsNullOrWhiteSpace(envInfo?.DetectedTimeZoneId))
                {
                    try
                    {
                        resolvedZone = TimeZoneInfo.FindSystemTimeZoneById(envInfo.DetectedTimeZoneId);
                    }
                    catch
                    {
                        resolvedZone = TimeZoneInfo.Local;
                    }
                }
                if (!string.IsNullOrWhiteSpace(envInfo?.DefaultLanguage))
                {
                    defaultLangCode = envInfo.DefaultLanguage;
                }
            }
        }
        catch
        {
            resolvedZone = TimeZoneInfo.Local;
        }

        // Always prefer live local OS timezone if valid, and persist timezone check result
        _activeTimeZone = TimeZoneInfo.Local ?? resolvedZone;

        if (store is not null)
        {
            try
            {
                var tzSnapshot = new
                {
                    id = _activeTimeZone.Id,
                    offsetHours = _activeTimeZone.BaseUtcOffset.TotalHours,
                    checkedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                };
                store.SetSettingJson(TimeZoneSettingKey, JsonSerializer.Serialize(tzSnapshot));
            }
            catch
            {
            }

            // 2. Load saved user language preference; default to English ("en") if not set
            try
            {
                string? savedLangJson = store.GetSettingJson(LanguageSettingKey);
                if (!string.IsNullOrWhiteSpace(savedLangJson))
                {
                    string? code = savedLangJson.StartsWith('"')
                        ? JsonSerializer.Deserialize<string>(savedLangJson)
                        : savedLangJson;
                    _language = IsChineseCode(code) ? AppLanguage.Chinese : AppLanguage.English;
                }
                else
                {
                    _language = IsChineseCode(defaultLangCode) ? AppLanguage.Chinese : AppLanguage.English;
                    store.SetSettingJson(LanguageSettingKey, JsonSerializer.Serialize(LanguageCode));
                }
            }
            catch
            {
                _language = AppLanguage.English;
            }

            // 3. Load saved visual style preference; default to Realistic sticky note style
            try
            {
                string? savedStyleJson = store.GetSettingJson(VisualStyleSettingKey);
                if (!string.IsNullOrWhiteSpace(savedStyleJson))
                {
                    string? styleCode = savedStyleJson.StartsWith('"')
                        ? JsonSerializer.Deserialize<string>(savedStyleJson)
                        : savedStyleJson;
                    _visualStyle = IsMinimalistCode(styleCode) ? AppVisualStyle.Minimalist : AppVisualStyle.Realistic;
                }
                else
                {
                    _visualStyle = AppVisualStyle.Minimalist;
                    store.SetSettingJson(VisualStyleSettingKey, JsonSerializer.Serialize(VisualStyleCode));
                }
            }
            catch
            {
                _visualStyle = AppVisualStyle.Minimalist;
            }
        }
        else
        {
            _language = AppLanguage.English;
            _visualStyle = AppVisualStyle.Minimalist;
        }

        NotifyAllChanged();
    }

    public void SetLanguage(AppLanguage language, ISettingsRepositoryPort? store = null)
    {
        if (_language == language)
        {
            if (store is not null)
            {
                try
                {
                    store.SetSettingJson(LanguageSettingKey, JsonSerializer.Serialize(LanguageCode));
                }
                catch
                {
                }
            }
            return;
        }

        _language = language;
        if (store is not null)
        {
            try
            {
                store.SetSettingJson(LanguageSettingKey, JsonSerializer.Serialize(LanguageCode));
            }
            catch
            {
            }
        }

        NotifyAllChanged();
    }

    public void SetVisualStyle(AppVisualStyle style, ISettingsRepositoryPort? store = null)
    {
        _visualStyle = style;
        if (store is not null)
        {
            try
            {
                store.SetSettingJson(VisualStyleSettingKey, JsonSerializer.Serialize(VisualStyleCode));
            }
            catch
            {
            }
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        VisualStyleChanged?.Invoke();
    }

    public void ToggleVisualStyle(ISettingsRepositoryPort? store = null)
    {
        SetVisualStyle(
            _visualStyle == AppVisualStyle.Realistic ? AppVisualStyle.Minimalist : AppVisualStyle.Realistic,
            store);
    }

    private static bool IsChineseCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) &&
        (code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(code, "chinese", StringComparison.OrdinalIgnoreCase));

    private static bool IsMinimalistCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) &&
        (code.StartsWith("min", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(code, "fluent", StringComparison.OrdinalIgnoreCase));

    private void NotifyAllChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        LanguageChanged?.Invoke();
        VisualStyleChanged?.Invoke();
    }

    public string DetectedTimeZoneSummary
    {
        get
        {
            var offset = _activeTimeZone.GetUtcOffset(DateTimeOffset.UtcNow);
            string sign = offset >= TimeSpan.Zero ? "+" : "-";
            string offsetText = $"UTC{sign}{offset:hh\\:mm}";
            return IsChinese
                ? $"当前时区: {offsetText} ({_activeTimeZone.Id})"
                : $"Time Zone: {offsetText} ({_activeTimeZone.Id})";
        }
    }

    // --- Common & Window Titles ---
    public string AppTitle => T("Sticky Notes", "电子便签");
    public string DetailWindowTitle => T("Details", "记录详情");
    public string ManagerWindowTitle => T("Calendar · Archive · Settings", "任务日历 · 历史归档 · 设置");

    // --- Main Panel Header & Bottom Bar ---
    public string HeaderDragTooltip => T(
        "Drag to move (when unlocked) · Right-click for quick menu",
        "左键按住拖动窗口（未锁定时）· 右键打开快捷设置菜单");
    public string PushpinTooltip => T(
        "Click pushpin: Pin to lock window / Unpin to move freely",
        "点击图钉：钉住锁定窗口位置 / 拔起自由拖动");
    public string KindSwitchTooltip => T("Click to switch TODO / Notes", "点击快速切换 待办 / 笔记");
    public string AddInlineTooltip => T("Add new item at bottom (Ctrl+N)", "在列表末尾新增空白输入行 (Ctrl+N)");
    public string SearchTooltip => T("Search (Ctrl+F)", "精确搜索 (Ctrl+F)");
    public string MoreOptionsTooltip => T("Style · View · Sort · Window Layer · Settings", "切换样式 · 事务切换 · 排序方式 · 桌面层级 · 设置");
    public string HideToTrayTooltip => T("Hide to system tray (use menu to quit)", "隐藏到系统右下角托盘 (彻底退出请通过菜单或托盘右键)");
    public string SearchPlaceholder => T("Search keywords... (Esc to clear)", "输入关键词搜索便签... (Esc 清空)");
    public string ClearSearchTooltip => T("Clear search", "清空搜索");
    public string EmptyAddTooltip => T("Click to add one", "点击新增一项");
    public string BottomAddText => T("+ New", "+ 新增");
    public string BottomAddTooltip => T("Add a new line at the bottom", "在列表末尾增加空白输入行");
    public string ArchiveCompletedTooltip => T(
        "Move all checked items to Archive",
        "点击确认归档：将所有已勾选完成的任务移出主面板并存入历史归档");

    // --- Main Panel Menus ---
    public string MenuStyleHeader => T("🎨  App Style (Realistic / Minimalist)", "🎨  应用样式 (写实 / 简约)");
    public string MenuStyleRealistic => T("📌  Realistic Sticky Note", "📌  写实便利贴样式");
    public string MenuStyleMinimalist => T("✨  Minimalist Style", "✨  简约现代样式");
    public string MenuSortHeader => T("↕️  Sort By", "↕️  排列方式");
    public string MenuSortDefault => T("Default (Priority → Due Date → Created)", "默认综合排序 (优先级 → 截止期 → 创建时间)");
    public string MenuSortDeadline => T("Due Date (Soonest First)", "按截止时间排序 (最近到期优先)");
    public string MenuSortPriority => T("Priority (Highest First)", "按优先级重要程度排序");
    public string MenuSortCreated => T("Created Time (Newest First)", "按创建时间排序 (最新创建优先)");
    public string MenuLayerHeader => T("📌  Window Layer & Lock", "📌  窗口桌面层级与锁定");
    public string MenuLanguageHeader => T("🌐  Language / 语言", "🌐  语言 / Language");
    public string MenuSwitchTasks => T("📋  TODO", "📋  待办事项");
    public string MenuSwitchNotes => T("📝  Notes", "📝  随手笔记");
    public string MenuQuickAddDetail => T("➕  New in Detail Window...", "➕  详情窗口新建...");
    public string MenuCalendarArchive => T("🎁  Calendar & Archive Box...", "🎁  任务日历与已归档收纳盒...");
    public string MenuOpenSettings => T("🎀  Settings & Style...", "🎀  打开设置与样式切换...");
    public string MenuArchiveAllDone => T("📦  Archive All Done Items", "📦  确认归档全部已完成事项");
    public string MenuSwitchTheme => T("🌓  Switch Theme (Light / Dark)", "🌓  切换明暗外观 (简约模式)");
    public string MenuHideToTray => T("🔽  Hide to System Tray", "🔽  隐藏到右下角系统托盘");
    public string MenuExitApp => T("🚪  Quit Sticky Notes", "🚪  彻底退出电子便签");

    // --- Task Row (TODO) ---
    public string RowDragHandleTooltip => T(
        "Drag onto another TODO to make this a sub-TODO",
        "按住拖动到其他任务上，可设为该任务的子任务");
    public string RowCheckTooltip => T(
        "Mark as done (stays in list until you click Archive)",
        "勾选标记为已完成（保留在列表内，确认归档后移出）");
    public string RowArchiveBtn => T("Archive", "归档");
    public string RowArchiveTooltip => T(
        "Move this completed item to Archive",
        "确认归档：将该已完成事项移出主面板并存入历史归档");
    public string RowSaveBtn => T("Save", "保存");
    public string RowSaveTooltip => T("Save (Enter)", "保存该条记录 (Enter)");
    public string RowCancelTooltip => T("Cancel (Esc)", "取消新增 (Esc)");
    public string RowAddSubtaskBtn => T("+ Sub-TODO", "+ 子任务");
    public string RowAddSubtaskTooltip => T("Add a sub-TODO under this TODO", "在此父任务下方直接新增一条子任务");
    public string RowOpenDetailBtn => T("Details ↗", "查看完整详情 ↗");
    public string RowOpenDetailTooltip => T("Open detail window", "打开编辑弹窗");
    public string RowMenuAddSubtask => T("➕  Add Sub-TODO", "➕  添加子任务");
    public string RowMenuEdit => T("✏️  Edit Content", "✏️  修改内容");
    public string RowMenuPromoteSubtask => T("⬆️  Make Main TODO", "⬆️  提升为独立主任务");
    public string RowMenuOpenDetail => T("📝  Open Details", "📝  查看完整详情");
    public string RowMenuArchive => T("📦  Archive Now", "📦  确认归档 (移出主面板)");
    public string RowMenuTrash => T("🗑️  Move to Trash", "🗑️  移入回收站");
    public string RowMenuCopyText => T("📋  Copy Text", "📋  复制文字");
    public string RowMenuPasteText => T("📋  Paste Text", "📋  粘贴文字");
    public string RowEditTooltip => T("Edit TODO title", "修改任务标题");
    public string RowDeleteBtn => T("🗑️ Trash", "🗑️ 移入回收站");
    public string RowDeleteTooltip => T("Move this TODO to trash", "将此任务移入回收站");
    public string MenuPromoteToRoot => T("⬆️  Promote to Root TODO", "⬆️  移出子任务 (转为顶层主任务)");
    public string NoticePromotedToRoot => T("Promoted to root TODO", "已将任务移出子任务，恢复为顶层主任务");
    public string MenuUndoAction => T("↩️  Undo (Ctrl+Z)", "↩️  撤销上一步操作 (Ctrl+Z)");
    public string NoticeActionUndone => T("Undone: ", "已撤销: ");
    public string NoticeNothingToUndo => T("Nothing to undo", "当前暂无可撤销的操作");
    public string SetUndoSectionTitle => T("↩️ Action History & Undo", "↩️ 操作历史与误操作撤销");
    public string SetUndoDesc => T(
        "Undo recent accidental actions (such as accidentally converting a task into a subtask, or deleting a task). Supports shortcut Ctrl+Z.",
        "支持快捷键 Ctrl+Z 或在此撤销近期误操作（如误将任务拖为子任务、误删事项等）。");
    public string SetUndoBtn => T("↩️ Undo Last Action (Ctrl+Z)", "↩️ 撤销上一步操作 (Ctrl+Z)");
    public string SetUndoEmpty => T("No recent operations to undo", "当前暂无可撤销的近期操作");

    // --- Detail Window ---
    public string DetailCloseTooltip => T("Close (Esc)", "关闭 (Esc)");
    public string DetailTitleLabel => T("Title", "标题");
    public string DetailTitlePlaceholder => T("Enter title...", "输入或修改任务/笔记标题...");
    public string DetailPriorityLabel => T("Priority", "优先级");
    public string DetailPrio0 => T("● P0 Urgent", "● P0 紧急");
    public string DetailPrio1 => T("● P1 High", "● P1 高");
    public string DetailPrio2 => T("● P2 Normal", "● P2 普通");
    public string DetailPrio3 => T("● P3 Low", "● P3 低");
    public string DetailDueLabel => T("Due Date", "截止期");
    public string DetailDueToday => T("Today", "今日");
    public string DetailDueTomorrow => T("Tomorrow", "明日");
    public string DetailDueNextWeek => T("Next Week", "下周");
    public string DetailDuePickPlaceholder => T("Pick date...", "指定日期...");
    public string DetailClearBtn => T("Clear", "清空");
    public string DetailStartLabel => T("Start Date", "开始时间");
    public string DetailStartSetToday => T("Set Today", "设为今日");
    public string DetailStartPickPlaceholder => T("Pick start date...", "选择开始日期...");
    public string DetailPeopleLabel => T("People", "人员信息");
    public string DetailAssigneeLabel => T("Owner:", "责任人:");
    public string DetailAssigneePlaceholder => T("Enter owner...", "填写责任人...");
    public string DetailPublisherLabel => T("From:", "发布人:");
    public string DetailPublisherPlaceholder => T("Enter sender...", "填写发布人...");
    public string DetailProgressLabel => T("Progress", "进度");
    public string DetailProgressAuto => T("Subtasks auto", "子任务统计");
    public string DetailProgressManual => T("Custom", "手动设置");
    public string DetailProgressDefault => T("Default (0%)", "默认 (0%)");
    public string DetailProgressReset => T("Follow subtasks", "跟随子任务");
    public string RowProgressLabel => T("Progress", "进度");
    public string RowProgressTooltip => T("Task progress (Click to cycle +25%)", "任务进度 (点击快速 +25%)");
    public string RowProgressCycleTooltip => T("Click to increment progress (+25%)", "点击增加进度 (+25%)");
    public string DetailBodyPlaceholder => T("Write details, steps, or notes here...", "输入详细描述、待办步骤或笔记正文...");
    public string DetailSubtasksLabel => T("↳ Sub-TODOs:", "↳ 子任务:");
    public string DetailSubtaskInputPlaceholder => T("Type a sub-TODO and press Enter...", "输入子任务名称后按 Enter 或点击右侧添加...");
    public string DetailAddSubtaskBtn => T("+ Add Sub-TODO", "+ 添加子任务");
    public string DetailSubtaskDoneBtn => T("Done", "完成");
    public string DetailDeleteBtn => T("Delete", "删除");
    public string DetailFilesLabel => T("📎 Files:", "📎 附件存储:");
    public string DetailSelectFolderBtn => T("Change Folder", "指定存储位置");
    public string DetailSelectFolderTooltip => T("Set a custom folder for this item's files", "为该记录单独指定相关文件的存储文件夹");
    public string DetailOpenFolderBtn => T("Open Folder", "打开目录");
    public string DetailAddFilesBtn => T("+ Add Files", "+ 添加文件");
    public string DetailFileOpenBtn => T("Open", "打开");
    public string DetailFileRemoveBtn => T("Remove", "移除");
    public string DetailDropHint => T(
        "Drag & drop files or folders here to copy them (original files are kept)",
        "可将文件或文件夹直接拖拽到此窗口，自动复制存入上方路径（保留源文件）");
    public string DetailFooterHint => T(
        "Auto-saves drafts · Drag files to attach · Ctrl+S to save",
        "修改自动暂存 · 支持拖入文件联动存储 · Ctrl+S 保存");
    public string DetailArchiveRecordBtn => T("📦 Archive", "📦 归档");
    public string DetailTrashRecordBtn => T("🗑️ Delete", "🗑️ 删除");
    public string DetailCloseBtn => T("Close", "关闭");
    public string DetailSaveBtn => T("Save", "保存修改");

    // --- Manager Window (Calendar / Archive / Settings) ---
    public string TabCalendar => T("📅 TODO Calendar", "📅 任务日历");
    public string TabArchiveTrash => T("📦 Archive & Trash", "📦 历史归档与回收站");
    public string TabSettings => T("⚙️ Settings", "⚙️ 设置与偏好");

    public string CalPrevMonth => T("◀ Prev", "◀ 上月");
    public string CalThisMonth => T("This Month", "回到本月");
    public string CalNextMonth => T("Next ▶", "下月 ▶");
    public string CalViewByLabel => T("View by:", "时间维度:");
    public string CalAxisDue => T("Due Date", "按截止期 (Deadline)");
    public string CalAxisStart => T("Start Date", "按开始时间");
    public string CalAxisCreated => T("Created Time", "按创建时间");
    public string CalAxisClosed => T("Archived Time", "按完成归档时间");
    public string CalEmptyTitle => T("No TODOs found for this month", "当前月份与维度下暂无任务记录");
    public string CalEmptySubtitle => T(
        "Try switching to another month or time filter above",
        "可切换上方月份或时间维度（截止期 / 开始时间 / 创建时间 / 归档时间）查看历史记录");

    // Weekday headers
    public string CalMon => T("Mon", "周一");
    public string CalTue => T("Tue", "周二");
    public string CalWed => T("Wed", "周三");
    public string CalThu => T("Thu", "周四");
    public string CalFri => T("Fri", "周五");
    public string CalSat => T("Sat", "周六");
    public string CalSun => T("Sun", "周日");

    // Calendar view switcher & markers
    public string CalViewMonthGrid => T("📅 Month Calendar", "📅 月历视图");
    public string CalViewList => T("📋 List Ledger", "📋 列表台账");
    public string CalCreatedLabel => T("Created", "创建");
    public string CalCompletedLabel => T("Completed", "完成");
    public string CalDueLabel => T("Due", "截止");
    public string CalHoverCreatedTitle => T("🌱 Created TODOs:", "🌱 本日创建任务:");
    public string CalHoverCompletedTitle => T("✅ Completed TODOs:", "✅ 本日完成任务:");
    public string CalHoverDueTitle => T("⏰ Due TODOs:", "⏰ 本日截止任务:");
    public string CalHoverEmpty => T("No TODO activity on this day", "该日期无任务记录");
    public string CalSelectedDateTitle => T("Selected Date TODOs", "选中日期任务清单");
    public string CalNoTasksSelectedDay => T("No TODOs on selected day", "选中日期暂无任务记录");
    public string TodayLabel => T("Today", "今日");

    public string HistArchivedBtn => T("✅ Archived Items", "✅ 已完成归档");
    public string HistTrashBtn => T("🗑️ Trash", "🗑️ 回收站");
    public string BtnEmptyTrash => T("🗑️ Empty Trash", "🗑️ 清空回收站");
    public string BtnEmptyTrashTooltip => T("Permanently delete all items in trash", "彻底清空回收站中的所有项目 (不可撤销)");
    public string ConfirmEmptyTrashTitle => T("Empty Trash Confirmation", "清空回收站确认");
    public string ConfirmEmptyTrashSubtitle => T("Permanent Deletion", "此操作不可撤销");
    public string ConfirmEmptyTrashMessage => T(
        "Are you sure you want to permanently empty the trash? All deleted tasks and notes will be permanently lost and cannot be recovered.",
        "确定要彻底清空回收站吗？清空后所有已删除的任务与笔记将永久丢失，此操作无法撤销。");
    public string ConfirmEmptyTrashBtn => T("Permanently Empty", "彻底清空");
    public string CancelBtn => T("Cancel", "取消");
    public string NoticeTrashEmptied => T("Trash emptied successfully", "已成功清空回收站");
    public string HistSearchPlaceholder => T("Search archive or trash...", "搜索历史归档或回收站...");
    public string HistReopenBtn => T("Reopen", "重新开启");
    public string HistReopenTooltip => T("Move back to active list", "将该已完成记录恢复到主面板列表");
    public string HistDeleteBtn => T("Delete", "删除");
    public string HistRestoreBtn => T("Restore", "还原记录");
    public string HistEmptyTitle => T("No archived or deleted items", "暂无相关归档或回收站记录");
    public string HistEmptySubtitle => T(
        "Archived items and deleted items will appear here",
        "在主面板确认归档或删除的记录会保存在此处，可随时查阅或恢复");

    // --- Settings Tab ---
    public string SetStyleSectionTitle => T("🎨 App Visual Style", "🎨 应用外观样式");
    public string SetStyleLabel => T("Sticky Note Visual Style", "便利贴界面样式");
    public string SetStyleDesc => T(
        "Switch between Realistic sticky note paper (Muyao handwriting font) and Minimalist modern style",
        "支持「写实纸质便利贴（沐瑶软笔手写体）」与「简约现代」两种样式实时切换");
    public string SetStyleRealisticBtn => T("📌 Realistic", "📌 写实样式");
    public string SetStyleMinimalistBtn => T("✨ Minimalist", "✨ 简约样式");

    public string SetLangSectionTitle => T("🌐 Language & Time Zone", "🌐 语言与时区设置");
    public string SetLangLabel => T("Display Language", "界面显示语言");
    public string SetLangDesc => T(
        "Switch between English (default) and Chinese immediately",
        "支持英文（默认）与中文实时切换，无需重启应用");
    public string SetLangEnBtn => T("English (Default)", "English (英文)");
    public string SetLangZhBtn => T("中文 (Chinese)", "中文 (简体)");

    public string SetWindowSectionTitle => T("Window & Desktop Behavior", "桌面悬浮层级与窗口行为");
    public string SetLayerLabel => T("Desktop Layer", "窗口桌面层级");
    public string SetLayerDesc => T(
        "Keep window always on top, or pin it to the desktop background",
        "支持悬浮在所有应用最上层（置顶），或嵌入在所有应用最下层（桌面层）");
    public string SetModeTopmostBtn => T("📌 Always on Top", "📌 最上层 (置顶)");
    public string SetModeDesktopBtn => T("🖥️ Desktop Back", "🖥️ 最下层 (桌面层)");
    public string SetLockLabel => T("Lock Window Position", "窗口位置锁定（禁止拖动应用）");
    public string SetLockDesc => T(
        "Prevent moving or resizing the main window on your desktop",
        "开启后锁定面板在桌面上的位置与尺寸，禁止鼠标拖拽移动窗口");
    public string SetStartupLabel => T("Launch at Login", "开机自动启动");
    public string SetStartupDesc => T(
        "Start Sticky Notes automatically when you sign in to Windows",
        "Windows 登录后自动启动电子便签并恢复上次位置与层级");

    public string SetAttachmentSectionTitle => T("📎 Default File Folder", "📎 全局默认附件存储位置");
    public string SetAttachmentDesc => T(
        "Dragged files are copied into this folder by default (original files are kept). Changes take effect immediately.",
        "当未单独指定专属目录时，拖拽或添加的文件将复制存储在此全局目录中（保留源文件），修改后立即热加载生效。");
    public string SetChangeFolderBtn => T("Change Folder...", "更改默认路径...");
    public string SetOpenFolderBtn => T("Open Folder", "打开目录");

    public string SetBackupSectionTitle => T("Backup & Restore", "数据安全与完整备份迁移");
    public string SetBackupDesc => T(
        "Export a full ZIP backup including your database and all attached files.",
        "完整备份将生成标准 ZIP 压缩包，内含 SQLite 数据库快照、所有关联附件文件及 SHA-256 校验清单。");
    public string SetExportBackupBtn => T("Export Backup (.zip)", "导出完整备份包 (.zip)");
    public string SetRestoreBackupBtn => T("Restore from Backup...", "从备份包恢复数据...");
}
