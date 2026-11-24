# 想法草案：基于 Emuera 生命周期的通用「汉化路线图 / 游戏流程引导」

> **状态**：构思与可行性验证归档（未合入 `main`，未来是否实现及具体交互形式待定）
> **归档日期**：2026-11-24

---

## 一、背景与痛点

大型 ERA 游戏往往包含 **1,000～2,500+ 个 ERB 脚本**和数百个 CSV 数据文件，新人或接手新坑的汉化者普遍面临两个问题：

1. **不知从何下手**：打开左侧文件树看到几百上千个文件，不知道哪几十个文件决定玩家「点开游戏后最先看到的画面」。
2. **「磁盘加载顺序」没有参考价值**：Emuera 启动时会把 `ERB/` 目录下所有脚本一次性全部加载进内存，因此磁盘加载顺序 ≠ 玩家真实游玩顺序。

**核心目标**：自动分析任意 ERA 游戏的真实执行主干，按**「玩家开机后最先遇到的游戏流程」**将字典文件分层排序，让汉化者先啃完前 10%～15% 的主骨架文件（立刻把标题、开局、主菜单、基础交互汉化完毕），再按模块/角色推进剩余海量分支内容。

---

## 二、技术路线对比与实测结论

### 1. 为什么不直接集成 `era_mcp`？
调研 [`passed1999/era_mcp`](https://github.com/passed1999/era_mcp) 后确认：
- 它是从 `Emuera.EM (EEv56)` 抽取的**无头加载与静态语法诊断工具**（CLI `era-analyze` / MCP `analyze_era_project`），用于检查 `IF/ENDIF`、标识符、参数等语法错误，**并非自动跑游戏交互的运行时模拟器**。
- 它包含完整的 Emuera 引擎解析层，大型项目分析耗时约 ~7 秒；直接塞进 EraTrans 会显著增加体积和维护复杂度。
- **未来定位**：`era_mcp` 非常适合后续作为可选的**「写回后 ERB 语法体检」**工具，但不适合作为汉化流程图的核心引擎。

### 2. 推荐方案：纯 C# 轻量级「Emuera 生命周期 + 静态调用图」分析器
所有 ERA 游戏（无论传统调教系、学园系还是 eratohoK 战略系）都遵循统一的 Emuera 内置生命周期入口：

```text
[Stage 0] CSV / ERD / XML 全局基础词表
    ↓
[Stage 1] 启动与开局：@SYSTEM_TITLE → @EVENTFIRST / @EVENTLOAD
    ↓ (BEGIN SHOP)
[Stage 2] 主界面与日常：@EVENTSHOP → @SHOW_SHOP → @USERSHOP / @EVENTBUY
    ↓ (BEGIN TRAIN)
[Stage 3] 核心交互与回合：@EVENTTRAIN → @SHOW_STATUS → @SHOW_USERCOM → @USERCOM → @EVENTEND / @EVENTTURNEND
    ↓ (CALLFORM 动态分发)
[Stage 4] 动态分支内容：指令(COM*) / 角色口上(KOJO*) / 剧情事件 / 技能
    ↓
[Stage 5] 其他 / 静态未触达文件
```

### 3. 在两款真实大型游戏上的实测数据（2026-11-24）

使用同目录的 `prototype_analyzer.py` 对两款架构完全不同的真实游戏进行全量实测：

| 指标 | `era_inSchool_統合調整版_ver1.1.4` | `eraFGOK私家改造0.00.50` |
| :--- | :--- | :--- |
| 游戏类型 | 传统学园 / 调教系 | `eratohoK` SLG 战略系 |
| 文件与函数规模 | 1,324 ERB · 1,944 CSV · 17,549 函数 | 2,496 ERB · 346 CSV · 19,674 函数 |
| Python 原型全量耗时 | **859 ms**（C# 预计 200~400 ms） | **976 ms**（C# 预计 200~400 ms） |
| 生命周期钩子识别 | `@SYSTEM_TITLE` / `@EVENTFIRST` / `@SHOW_SHOP` / `@USERSHOP` / `@EVENTTRAIN` / `@SHOW_STATUS` / `@USERCOM` 等**全部命中** | 同左，**全部命中** |
| 纯静态 `CALL/JUMP/BEGIN` 可达 | **179 / 1,324（13.5%）**（精准锁定核心骨架） | **294 / 2,496（11.8%）**（精准锁定核心骨架） |
| 叠加 `CALLFORM` 前缀匹配可达 | **966 / 1,324（73.0%）** | **2,397 / 2,496（96.0%）** |

**实测抓出的开机最先触达文件（BFS from `@SYSTEM_TITLE` / `@EVENTFIRST`）**：
- **`era_inSchool`**：
  - `depth=0`：`ERB/SYSTEM.ERB`（标题与开局入口）
  - `depth=1`：`ERB/OPENING.ERB`、`ERB/SET_MASTER.ERB`、`ERB/SAVELOAD.ERB`、`ERB/OPTION.ERB`、`ERB/SHOP.ERB`、`ERB/SHOP2.ERB`
  - 主界面：`ERB/SHOP.ERB`；核心调教：`ERB/INFO.ERB`、`ERB/USERCOM.ERB`、`ERB/EVENT_T.ERB`
- **`eraFGOK`**：
  - `depth=0`：`ERB/SYSTEM/NEWGAME.ERB`
  - `depth=1`：`ERB/SYSTEM/NEWLOOP.ERB`、`ERB/SHOP/SHOP.ERB`、`ERB/SYSTEM/SAVELOAD.ERB`、`ERB/SYSTEM/CUSTOM_CHARA/CUSTOM_CHARA.ERB`、`ERB/SYSTEM/CONFIG_HELP.ERB`
  - 核心调教：`ERB/TRAIN/SYSTEM_TRAIN.ERB`、`ERB/TRAIN/SHOW_STATUS.ERB`、`ERB/TRAIN/USERCOM.ERB`、`ERB/SYSTEM/TURNEND.ERB`

---

## 三、实测发现的关键坑点与解法

1. **纯逻辑初始化文件干扰**
   - 现象：如 `ERB/SETUP_APPLICATION_DATA.ERB` 处于 `depth=1`，但包含 `0` 条玩家可见打印文本。
   - 解法：与 EraTrans 现有字典列表及 `DictionaryProgressScanner` 联动，**仅展示存在对应字典且有待翻词条的文件**，无文本纯逻辑文件自动过滤。
2. **避免 1,000+ 节点的蜘蛛网图**
   - 现象：把全部文件画成节点连线图会糊成一团、完全不可操作。
   - 解法：采用**「顶部 6 阶段可点击流程图卡片 + 下方该阶段排序文件表」**的交互形态；`CALLFORM` 命中的海量文件（如角色口上/技能）归入阶段 4，支持按目录/关键词再筛选。
3. **只读原游戏与编码兼容**
   - 分析器复用 `GameFileText` 的无损编码探测（自动跳过编码异常文件），严格只读原游戏目录。

---

## 四、预计 UI 与交互设计

> 同目录附带静态可视化原型 `ui-mockup.html`，可直接用浏览器打开预览。

### 1. 入口位置（两个备选方案，未来按需二选一）
- **方案 A**：在编辑器工具栏「翻译统计」旁边新增独立按钮「汉化路线」。
- **方案 B**：合并进现有的 `TranslationProgressWindow`（「翻译统计」弹窗），顶部加两个 Tab：`按文件统计` | `按游戏流程路线`。

### 2. 界面线框图（WPF 窗口）

```text
+--------------------------------------------------------------------------------------+
|  游戏流程汉化路线图                                                 [刷新分析] [关闭] |
|  工作区：D:\Output\eraFGOK\V2\字典  ·  已分析 2,496 个 ERB（耗时 310 ms）              |
+--------------------------------------------------------------------------------------+
|  [0. 基础词表]  →  [1. 启动与开局]  →  [2. 主界面/日常]  →  [3. 核心循环]  →  [4. 动态分支]  →  [5. 其他] |
|  CSV / ERD         SYSTEM_TITLE       SHOW_SHOP           EVENTTRAIN       CALLFORM 聚类      未触达   |
|  12 / 48 文件       8 / 12 文件        15 / 36 文件         4 / 52 文件      120 / 1,800       0 / 90   |
|  [████░░ 25%]      [██████ 67%]       [████░░ 42%]        [█░░░░░ 8%]      [░░░░░░ 6%]       [░░░░]   |
+--------------------------------------------------------------------------------------+
|  当前阶段：1. 启动与开局（建议优先汉化，完成后标题/开场/读档全中文）                      |
|  [搜索文件路径…]                                          [只看未完成 ✓] [全部状态 ▾]   |
+--------------------------------------------------------------------------------------+
| 序号 | 字典文件                       | 入口来源 / 深度         | 待翻 | 已翻 | 完成度 | 状态   |
+------+--------------------------------+-------------------------+------+------+--------+--------+
|  1   | ERB\SYSTEM\NEWGAME.json        | @SYSTEM_TITLE (0跳)     |   12 |   28 |  70.0% | 翻译中 |
|  2   | ERB\SYSTEM\NEWLOOP.json        | @EVENTFIRST → 1跳       |  180 |   47 |  20.7% | 翻译中 |
|  3   | ERB\SYSTEM\SAVELOAD.json       | @EVENTLOAD (0跳)        |    0 |   14 | 100.0% | 已完成 |
|  4   | ERB\SYSTEM\CUSTOM_CHARA\...    | @EVENTFIRST → 1跳       |    9 |    0 |   0.0% | 未翻译 |
+--------------------------------------------------------------------------------------+
|  提示：按真实游戏执行顺序（入口深度优先）排列；双击任意行直接跳转到编辑器开始汉化。          |
|                                                                [跳转到选中文件] [关闭] |
+--------------------------------------------------------------------------------------+
```

---

## 五、未来 C# 落地模块拆分草案

如果未来决定合入 EraTrans，建议按以下最小侵入边界实现：

1. **`src/EraTrans.Core/GameFlowAnalyzer.cs`**（纯 C#、零外部依赖）：
   - `enum GameFlowStage { BaseData = 0, Startup = 1, MainHub = 2, CoreLoop = 3, DynamicBranches = 4, Unreached = 5 }`
   - `record GameFlowFileNode(string RelativeDictionaryPath, GameFlowStage Stage, int HopDepth, string EntryReason, DictionaryFileProgress Progress)`
   - `record GameFlowStageSummary(GameFlowStage Stage, string Title, string Subtitle, IReadOnlyList<GameFlowFileNode> Files, ...)`
   - `GameFlowAnalyzer.Analyze(string gameDirectory, IReadOnlyList<DictionaryFileProgress> dictionaryProgress, Encoding fallbackEncoding)`
2. **`src/EraTrans.App/TranslationRoadmapWindow.xaml` & `.xaml.cs`**（或作为 `TranslationProgressWindow` 的第二个 Tab）：
   - 顶部横向 6 张可点击阶段卡片（带箭头连接、进度条、选中态边框高亮）。
   - 下方 `DataGrid`（支持搜索、只看未完成、双击跳转回 `DictionaryEditorControl.NavigateToDictionaryFile`）。
3. **`tests/EraTrans.Tests/GameFlowAnalyzerTests.cs`**：
   - 验证 `@SYSTEM_TITLE` / `@EVENTFIRST` / `BEGIN SHOP` / `BEGIN TRAIN` 跨文件调用链的阶段归属与跳数计算。
   - 验证 `CALLFORM` 前缀匹配归入 Stage 4，以及无对应字典的纯逻辑文件被自动过滤。

### 红线核查
- 不改动 `LegacyParser/`。
- 不改动主字典 JSON 5 字段结构（分析结果纯内存计算，不写入字典文件）。
- 不改动 AI 响应解析逻辑。
- 原游戏目录严格只读。
