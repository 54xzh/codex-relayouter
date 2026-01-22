# 技术设计: 多任务并行 + 会话状态保持 + 侧边栏状态指示

## 实现摘要
- Bridge Server：移除“全局单任务”限制，改为按 `runId` 并发跟踪；同 `sessionId` 串行闸门；`run.cancel` 支持 `runId/sessionId`；审批按 `(runId, requestId)` 并发；对每个 WebSocket 连接串行化 SendAsync
- WinUI：引入全局 `ChatSessionStore` 缓存会话 Messages/计划/运行状态/徽章，页面只绑定 Store，切换页面/会话不丢输出；侧边栏会话项右侧显示 ProgressRing/绿点/黄点；审批弹窗在 MainWindow 统一处理
- 体验修复：禁用消息列表 `ListView.ItemContainerTransitions`，避免切换会话时大量条目触发不自然的入场动画
- 文档同步：协议与数据模型文档、模块文档与 `helloagents/CHANGELOG.md`

## 关键文件
- `codex-bridge-server/Bridge/WebSocketHub.cs`
- `codex-bridge/State/ChatSessionStore.cs`
- `codex-bridge/MainWindow.xaml.cs`
- `codex-bridge/Pages/ChatPage.xaml.cs`
- `codex-bridge/Pages/ChatPage.xaml`

## 验证
- `dotnet test codex-bridge-server.Tests/codex-bridge-server.Tests.csproj -c Release`
- `dotnet test codex-bridge-common.Tests/codex-bridge-common.Tests.csproj -c Release`
- `dotnet build codex-bridge/codex-bridge.csproj -c Release -p:Platform=x64`

