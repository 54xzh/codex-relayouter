# 轻量迭代：Chat 状态按钮（/status）

> **状态**：已完成  
> **范围**：WinUI Chat 页 + Bridge Server  

## 任务清单

- [√] 在 Bridge Server 新增 `GET /status`，返回可读的状态摘要（不泄露 BearerToken 明文）
- [√] WinUI Chat 页右下角新增“状态”按钮，点击后请求 `/status` 并弹窗展示内容
- [√] 更新知识库：补充 API 文档与变更记录
- [√] 质量验证：本地构建 `codex-bridge-server` 与 `codex-bridge`

## 验证
- `dotnet build codex-bridge-server/codex-bridge-server.csproj -c Release`
- `dotnet build codex-bridge/codex-bridge.csproj -c Release -p:Platform=x64`
