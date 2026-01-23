# Android 聊天：执行过程（Trace）展示与折叠对齐

## 背景
当前 Android 聊天页仍以“气泡”样式渲染 assistant 消息，且缺少对运行事件（思考/命令/diff）的可视化展示；Windows（WinUI）端已具备“执行过程（Trace）”展开器与折叠策略。

## 目标
- assistant 消息去除气泡背景，正文直接显示在页面背景上（更接近 Windows 端阅读体验）。
- Android 端新增“执行过程（Trace）”展示：
  - 思考过程：`run.reasoning` / `run.reasoning.delta`
  - 执行命令：`run.command` / `run.command.outputDelta`
  - 内联 diff：`diff.updated`（可选，但用于与 Windows 端折叠策略对齐）
- 折叠策略对齐 Windows 端：历史默认折叠；运行中随事件增量更新；reasoning 自动展开最新条目，diff 默认展开。

## 范围
### 范围内
- Android：聊天 UI（assistant 样式、Trace UI、折叠/展开交互）
- Android：解析历史消息的 `trace` 字段并展示
- Android：补齐 WS 事件处理（run/diff 事件）
- 测试：补充 Trace 解析/折叠策略的单元测试
- 文档：更新 Android Client 模块文档与 CHANGELOG

### 范围外
- Bridge Server 协议变更（仅消费既有字段与事件）
- Android Markdown 渲染/图片展示等非本需求点

## 方案与关键决策
- **数据层：**扩展 Android 的 `SessionMessage`，支持 `trace` 反序列化（Gson）。
- **运行中增量：**对 `run.started/run.completed/run.failed/run.canceled` 建立 run→message 关联；对 `run.command/run.reasoning/diff.updated` 进行增量 upsert。
- **UI：**
  - assistant：纯文本 + Trace 区块（Card + 可折叠）
  - Trace 条目：按 kind 分三类可展开块（command/reasoning/diff）

## 验收标准
- Android 聊天页：assistant 消息无气泡背景。
- Android 聊天页：可查看思考过程与执行命令；折叠行为与 Windows 端一致。
- `codex-relayouter-android` 单元测试通过（`./gradlew test`）。
