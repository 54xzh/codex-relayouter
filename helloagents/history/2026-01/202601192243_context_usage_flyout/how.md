# 技术设计: 上下文用量 Flyout 与限额进度条

## 后端（/status）
- 继续使用 `GET /status` 返回纯文本，字段保持精简：`5h限额`、`周限额`、`上下文用量`
- 调整重置时间格式：`MM-dd HH:mm`（本地时间，不显示时区）

## 前端（WinUI）

### Flyout 交互
- 点击按钮时创建并显示 `Flyout`，锚定到按钮位置
- Flyout 内容为一个轻量面板（StackPanel/ProgressBar/TextBlock），展示：
  1. 后端连接：基于 `App.ConnectionService.IsConnected`
  2. 5h限额：进度条 + 文本（使用百分比、重置时间）
  3. 周限额：进度条 + 文本（使用百分比、重置时间）
  4. 上下文用量：文本（与按钮一致）
- 字体策略：不在 Flyout 内容控件上设置 `FontFamily`，使用全局默认字体，避免终端字体导致的繁体 fallback

### 数据解析
- 从 `/status` 文本中解析：
  - `上下文用量:` → 按钮百分比与 Flyout 显示
  - `5h限额:` / `周限额:` → `used_percent` 与 `resetsAt`（根据固定输出模板提取）
- 缺失处理：
  - used_percent 缺失 → 进度条设为 `IsIndeterminate=true`，百分比显示 `-%`（或“不可用”）
  - 重置时间缺失 → 显示“不可用”

## 安全与性能
- /status 仍只输出统计与时间信息，不包含对话内容/密钥
- Flyout 每次点击拉取一次 /status（也可复用按钮刷新结果）

