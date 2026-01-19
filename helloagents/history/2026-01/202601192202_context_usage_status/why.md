# 变更提案: 上下文用量精简展示

## 需求背景
当前 Chat 页右下角按钮用于展示 `/status` 文本，但信息项较多，且按钮文案无法直观看到“上下文用量”。
需要将展示内容精简为对用户最有价值的四项，并让按钮直接显示上下文用量百分比。

## 变更内容
1. 精简展示内容：仅保留
   - 与后端连接状态
   - 5h 限额
   - 周限额
   - 已用上下文用量
2. 按钮文案改为上下文用量百分比（例如 `39%`）；若无法获取则显示 `-%`。

## 成功标准
- 当可从会话中解析到 token_count 信息时：
  - 按钮显示类似 `15%` 的数字（无小数）
  - 弹窗仅显示 4 行（连接状态/5h/周/上下文用量），且内容可复制、可滚动
- 当无法解析到 token_count 信息时：
  - 按钮显示 `-%`
  - 弹窗中“上下文用量”显示为不可用（或 `-%`，与按钮保持一致）

## 影响范围
- **模块:**
  - Bridge Server（`GET /status` 输出与解析）
  - WinUI Client（按钮文案与弹窗展示）
- **文件（预估）:**
  - `codex-bridge-server/Bridge/StatusTextBuilder.cs`
  - `codex-bridge-server/Program.cs`
  - `codex-bridge/Pages/ChatPage.xaml`
  - `codex-bridge/Pages/ChatPage.xaml.cs`
  - `helloagents/wiki/api.md`
  - `helloagents/wiki/modules/bridge-server.md`
  - `helloagents/wiki/modules/winui-client.md`
  - `helloagents/CHANGELOG.md`

## 风险评估
- **风险:** “上下文用量百分比”的计算口径与用户预期不一致。
- **缓解:** 明确采用 token_count 的 `last_token_usage` 与 `model_context_window` 计算，并在后续可切换为 input/total 口径。

