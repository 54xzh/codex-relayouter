# 自动翻译：思考过程（Trace）与摘要

## 背景
当前 Bridge Server 会将 `codex app-server` 的运行事件（`run.reasoning`/`run.reasoning.delta` 等）通过 WS 广播给 WinUI/Android 等客户端，并在历史回放时从 `~/.codex/sessions/*.jsonl` 解析 `agent_reasoning` 与 `reasoning` 相关条目作为 Trace 展示。

在多端同步查看同一会话时，如果 Trace/摘要主要为英文（或非目标语言），阅读体验较差；但又不能改写 `~/.codex/sessions` 的原始 JSONL（需要保持可回放与可审计）。

## 目标
- **独立翻译 API 配置：**使用单独的 `baseUrl + apiKey`（不复用 Codex 主链路的配置）。
- **功能开关：**提供服务端**全局**开关，可一键启用/禁用自动翻译。
- **按段翻译且不干扰流式：**以“每条 Trace reasoning 条目 / 每个 reasoning summary part”为单位；不翻译 `run.reasoning.delta` 这类仍在流式增长的片段。
- **后端完成替换/广播：**翻译与文本替换在后端完成并广播，使多个客户端最终看到一致的翻译结果。
- **独立存储：**翻译结果单独持久化（缓存），不写入/不修改 `~/.codex/sessions` 的 session 文件。

## 范围
### 范围内
- Bridge Server：自动翻译配置、翻译任务调度、缓存存储、WS 广播更新、历史回放附带翻译（或按需替换）。
- 协议：必要时补充事件字段/新事件（确保多端一致更新）。
- 测试：服务端单元测试覆盖（缓存命中/去重、不翻译 delta、不会写入 session JSONL）。
- 文档：更新 API/Protocol/Bridge Server 模块文档（说明开关、行为与安全注意事项）。

### 范围外（暂不做）
- 翻译 assistant 最终回答正文（`chat.message` / `chat.message.delta`）。
- 翻译 command/output/diff 等非 reasoning 文本（默认不翻译，避免成本与误译代码/日志）。
- 多语言自动检测/多目标语言同时缓存（可后续扩展）。

## 方案与关键决策

### 1) 配置（服务端）
在 `codex-relayouter-server/appsettings.json` 增加：
- `Bridge:Translation:Enabled`（bool）
- `Bridge:Translation:BaseUrl`（string；可为根地址或包含 `/v1`，服务端需做归一化）
- `Bridge:Translation:ApiKey`（string，建议支持环境变量覆盖，避免明文落盘）
- `Bridge:Translation:TargetLocale`（固定为 `zh-CN`；不提供多语言切换）
- `Bridge:Translation:Model`（必填；OpenAI 兼容接口需提供 model）
- `Bridge:Translation:MaxRequestsPerSecond`（int；每秒最大请求数，默认 `1`）
- `Bridge:Translation:MaxConcurrency`（int；最大并发翻译请求数，默认 `2`）
- `Bridge:Translation:TimeoutMs`、`MaxInputChars`（可选）

开关语义建议：
- `Enabled=false`：完全关闭（不调用翻译 API、不读写翻译缓存、也不做替换广播）。
- `Enabled=true`：开启（命中缓存则直接替换；未命中则后台翻译后再替换/广播）。

### 2) 翻译单位与触发时机
**单位：**以“完成态”的 reasoning 文本为单位。
- 运行中：以 `run.reasoning(itemId, text)` 为单位（每个 `itemId` 对应一个 summary part）。
- 历史回放：以 `message.trace[kind=reasoning]` 的每条条目为单位（每条条目包含 `title/text`）。

**禁止：**不在 `run.reasoning.delta` 到达时翻译（避免对仍在增长的文本做重复翻译）。

### 3) 后端替换与多端一致
为尽量减少客户端改动，优先采用“**复用既有事件做 upsert**”的方式：
- 服务端仍立即广播原始 `run.reasoning`（保证低延迟显示）。
- 翻译完成后，服务端再次广播 **同名事件** `run.reasoning`，使用相同 `itemId`，但将 `text` 替换为译文（可选附带 `translated=true/locale=...` 字段）。
  - 现有客户端对 `run.reasoning` 已是 upsert（同 `itemId` 会覆盖），因此能自然完成“文本替换”，并同步给所有连接的客户端。

历史回放（HTTP）建议两种模式二选一：
1. **替换返回（强一致）：**当缓存命中时，直接在返回的 `trace.title/text` 中用译文替换；未命中则返回原文（并后台补齐缓存）。
2. **双字段返回（更友好）：**响应增加可选字段 `trace.translatedTitle/translatedText`（或 `trace.i18n`），客户端可做“原文/译文”切换；但需要客户端支持。

### 4) 独立存储（不改 session 文件）
新增翻译缓存文件（建议与设备配对存储同目录）：
- 路径：`%LOCALAPPDATA%\\codex-relayouter\\translations.json`（或 `translations/` 子目录）
- 结构：版本化 JSON，按 `targetLocale + sourceSha256` 做 key，存储译文与元数据（provider/model/时间戳）。
- 行为：仅写入该缓存文件；严禁写入/修改 `~/.codex/sessions/*.jsonl`。

### 5) 翻译 API 适配（可插拔）
考虑用户提供的是 `baseUrl + apiKey`，默认实现采用 **OpenAI 兼容 Chat Completions**：
- `POST {BaseUrl}/v1/chat/completions`
- `Authorization: Bearer {ApiKey}`
- 提示词约束：输出固定为 `zh-CN`，尽量保留 Markdown 结构/代码块/命令与路径不被翻译或改写。

## 失败与降级策略
- 配置缺失（BaseUrl/ApiKey 为空）或调用失败：仅记录日志，保持原文，不影响运行链路与 WS 流式输出。
- 翻译并发与速率：受 `MaxConcurrency` 与 `MaxRequestsPerSecond` 双重限制；超额请求排队等待，不阻塞 WS 广播与主链路。
- 去重：相同 sourceHash 命中缓存直接复用，避免重复付费翻译。

## 已确认参数
1. 目标语言：固定为 `zh-CN`
2. 翻译 API：OpenAI 兼容
3. 开关作用域：服务端全局

## 验收标准
- 开启翻译后：每条 `run.reasoning` 在完成后会被服务端异步翻译并通过 WS 广播覆盖更新，所有在线客户端最终显示译文。
- `run.reasoning.delta` 不触发翻译调用，流式输出性能不受明显影响。
- 翻译结果持久化到独立缓存文件；`~/.codex/sessions` 的 JSONL 不发生任何写入或内容变化。
