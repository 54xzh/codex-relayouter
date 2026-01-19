# 轻量迭代：/status 指令输出增强

> **状态**：已完成  
> **范围**：WinUI Chat 页 + Bridge Server + 知识库  

## 目标
- Chat 页点击“状态”后，展示结构化 `/status` 输出，至少包含：
  - 模型 / 目录 / 批准策略 / 沙盒 / 账户 / session id / AGENTS.md / 5h 限额 / 周限额
- 任意字段无法获取时，该字段值显示为“不可用”

## 任务清单
- [√] Bridge Server：增强 `GET /status` 输出（结构化字段 + 不可用占位 + 不泄露密钥）
- [√] Bridge Server：从 `~/.codex/sessions/<session>.jsonl` 解析 rate_limits（5h/周），并提取 AGENTS.md 来源信息（如可得）
- [√] WinUI：`/status` 请求附带当前 `sessionId/workingDirectory/model/sandbox/approvalPolicy`
- [√] 知识库：更新 API 手册与模块文档 + 变更记录
- [√] 质量验证：构建 `codex-bridge-server`、`codex-bridge`

## 验证
- `dotnet build codex-bridge-server/codex-bridge-server.csproj -c Release`
- `dotnet build codex-bridge/codex-bridge.csproj -c Release -p:Platform=x64`
