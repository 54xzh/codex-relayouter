# 任务清单: 多任务并行 + 状态保持 + 侧边栏指示

- [√] Bridge Server：支持跨会话并行 run（同会话串行闸门）
- [√] Bridge Server：`run.cancel` 支持指定 `runId/sessionId`
- [√] Bridge Server：并发审批请求改为按 `runId/requestId` 跟踪
- [√] Bridge Server：Broadcast 对每个 WebSocket 串行化发送
- [√] WinUI：新增全局 Session/Run Store 与事件路由（含 `runId -> sessionId` 映射与事件缓冲）
- [√] WinUI：ChatPage 改为从 Store 渲染并按 session 维度管理运行状态
- [√] WinUI：MainWindow 修复 ChatPage 重复 Navigate/多实例订阅问题
- [√] WinUI：侧边栏会话项右侧增加 ProgressRing/绿点/黄点指示器（按优先级渲染）
- [√] 测试：补充 server 并行 run / cancel / approval 覆盖
- [√] 文档：更新 `helloagents/wiki/api.md` 与 `helloagents/wiki/data.md`（必要时 `helloagents/wiki/arch.md`）
- [√] 文档：实现后同步 `helloagents/CHANGELOG.md`
- [√] WinUI：修复会话切换动画异常（禁用消息列表默认 ItemContainerTransitions）
