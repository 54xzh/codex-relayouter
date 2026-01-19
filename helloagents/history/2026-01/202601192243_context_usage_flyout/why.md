# 变更提案: 上下文用量 Flyout 与限额进度条

## 需求背景
当前“上下文用量”按钮点击后使用对话框展示信息，交互偏重；同时限额信息以纯文本呈现，不够直观。

## 变更内容
1. 将点击后的展示从对话框改为菜单式 Flyout（类似下拉菜单），避免全屏/大弹窗打断。
2. 将 5h 限额与周限额以进度条方式可视化展示（同时保留百分比与重置时间）。
3. 统一重置时间输出格式为 `MM-dd HH:mm`，不显示时区。

## 成功标准
- 点击右下角按钮弹出 Flyout，展示 4 项：后端连接状态 / 5h限额 / 周限额 / 上下文用量
- 5h/周限额以进度条展示，文本显示 `使用 X%` 与 `重置 MM-dd HH:mm`（缺失项显示“不可用”）
- 按钮文案仍为上下文用量百分比（无数据为 `-%`），且 UI 使用全局默认字体（不使用终端字体/繁体回退字体）

## 影响范围
- **模块:**
  - Bridge Server（`/status` 输出格式）
  - WinUI Client（Flyout UI 与限额进度条）
- **文件（预估）:**
  - `codex-bridge-server/Bridge/StatusTextBuilder.cs`
  - `codex-bridge/Pages/ChatPage.xaml.cs`
  - `helloagents/wiki/modules/winui-client.md`
  - `helloagents/CHANGELOG.md`
  - `helloagents/history/index.md`

