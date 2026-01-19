# 任务清单: 修复无正文回复的历史丢失

目录: `helloagents/plan/202601200021_fix_incomplete_reply_history/`

---

## 1. Bridge Server 会话回放
- [√] 1.1 解析 `event_msg.agent_message` 作为 assistant 输出兜底（兼容不同版本/来源的 sessions）
- [√] 1.2 会话末尾仍有 trace 时，刷出占位 assistant 消息（避免中断导致“无正文”记录丢失）
- [√] 1.3 避免 `agent_message` 与 `response_item.message(role=assistant)` 重复回放

## 2. WinUI 历史回放
- [√] 2.1 Chat 页加载历史时保留“无正文但有 Trace/图片”的消息

## 3. 回归验证
- [√] 3.1 新增 xUnit 测试覆盖：trace 末尾刷出、agent_message 兜底、去重
- [√] 3.2 构建验证：`dotnet test codex-bridge-server.Tests` + `dotnet build -p:Platform=x64`

