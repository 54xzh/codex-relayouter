# 技术设计: 上下文用量精简展示

## 方案选择
采用方案 A：保留现有 `GET /status`（纯文本），但将输出精简为 3 项；WinUI 弹窗顶部本地补充“后端连接状态”行，并将按钮文案显示为上下文用量百分比。

## API 设计
### [GET] /status
**返回类型:** `text/plain; charset=utf-8`

**输出（仅 3 行）:**
- `5h限额: ...`
- `周限额: ...`
- `上下文用量: 39%`（不可用时：`上下文用量: 不可用`）

> 说明：连接状态由 WinUI 端根据当前 WebSocket 连接状态计算并展示，不作为 `/status` 输出项。

## 数据来源与计算

### 5h/周限额
- 数据来源：会话 JSONL（`~/.codex/sessions/**/<session>.jsonl`）中最近一条 `event_msg` 且 `payload.type=token_count` 的 `payload.rate_limits`。
- 解析方式：读取 session JSONL 末尾（例如 512KB）并从后向前查找最近 token_count。
- 映射规则：`window_minutes=300` → 5h；`window_minutes=10080` → 周。
- 输出字段：`used_percent` 与 `resets_at`（Unix 秒时间戳，转本地时间）。

### 上下文用量（按钮百分比）
- 数据来源：同一条 token_count 的 `payload.info`。
- 计算口径（默认）：`percent = round( last_token_usage.input_tokens / model_context_window * 100 )`
  - 若 `model_context_window` 或 `input_tokens` 缺失 → 不可用
  - 结果限制在 `[0, 100]`，按钮显示整数百分比（例如 `39%`）
- 备选口径（如需更贴近“窗口总占用”）：用 `last_token_usage.total_tokens` 替代 `input_tokens`。

## WinUI 展示与交互

### 按钮文案
- 默认显示 `-%`。
- 页面加载完成后与每次 run 结束后，调用一次 `/status`：
  - 解析 `上下文用量:` 行提取百分比并更新按钮 `Content`（如 `39%`）
  - 无法解析时保持 `-%`

### 弹窗内容
- 弹窗只展示 4 行：
  1. `后端连接: 已连接/未连接`（基于 `App.ConnectionService.IsConnected`）
  2. `5h限额: ...`
  3. `周限额: ...`
  4. `上下文用量: ...`
- 保持滚动与文本选择（`ScrollViewer` + `TextBlock`）。

## 安全与性能
- **安全:** 仅输出统计信息，不包含任何敏感凭据与提示词内容。
- **性能:** `/status` 仅做尾部扫描与单条 JSON 解析，成本较低；WinUI 刷新频率控制在“页面加载 + run结束”。

