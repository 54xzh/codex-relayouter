# 任务清单: 状态弹窗排版与 /status 输出精简

目录: `helloagents/plan/202601192057_status_popup_layout/`

---

## 1. Bridge Server（/status）
- [√] 1.1 在 `codex-bridge-server/Bridge/StatusTextBuilder.cs` 中移除 `AGENTS.md` 输出行，并删除相关解析/查找逻辑，保持缺失项回退为“不可用”

## 2. WinUI Client（状态弹窗）
- [√] 2.1 在 `codex-bridge/Pages/ChatPage.xaml.cs` 中加宽状态弹窗，并调整 `TextBlock` 的行高/字间距，保持滚动与文本选择可用

## 3. 文档更新
- [√] 3.1 更新 `helloagents/wiki/api.md`：/status 描述移除 `AGENTS.md`
- [√] 3.2 更新 `helloagents/wiki/modules/bridge-server.md`：/status 描述移除 `AGENTS.md`
- [√] 3.3 更新 `helloagents/wiki/modules/winui-client.md`：状态按钮描述移除 `AGENTS.md`
- [√] 3.4 更新 `helloagents/CHANGELOG.md`：状态查询描述移除 `AGENTS.md` 并记录排版优化

## 4. 质量验证
- [√] 4.1 执行构建：`dotnet build codex-bridge-server/codex-bridge-server.csproj -c Release`
- [√] 4.2 执行构建：`dotnet build codex-bridge/codex-bridge.csproj -c Release -p:Platform=x64`

## 5. 安全检查
- [√] 5.1 检查 `/status` 输出不包含任何明文密钥/令牌；账户仅输出摘要
