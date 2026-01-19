# 任务清单: 上下文用量精简展示

目录: `helloagents/plan/202601192202_context_usage_status/`

---

## 1. Bridge Server（/status 精简 + 上下文用量）
- [√] 1.1 在 `codex-bridge-server/Bridge/StatusTextBuilder.cs` 中将 `/status` 输出精简为 3 行：5h/周/上下文用量
- [√] 1.2 扩展 session JSONL 的 token_count 解析：读取 `payload.info.model_context_window` 与 `payload.info.last_token_usage.input_tokens`，计算上下文用量百分比
- [√] 1.3 处理缺失字段：无法获取时输出“不可用”

## 2. WinUI Client（按钮显示百分比 + 弹窗仅 4 行）
- [√] 2.1 在 `codex-bridge/Pages/ChatPage.xaml` 将按钮初始文案设为 `-%`，Tooltip 调整为“上下文用量”
- [√] 2.2 在 `codex-bridge/Pages/ChatPage.xaml.cs` 增加刷新逻辑：页面加载完成与 run 结束后调用 `/status`，解析“上下文用量”并更新按钮文案
- [√] 2.3 点击按钮弹窗展示：本地连接状态 + `/status` 3 行（共 4 行），保持滚动与文本选择

## 3. 文档更新
- [√] 3.1 更新 `helloagents/wiki/api.md`：/status 说明同步为精简字段
- [√] 3.2 更新 `helloagents/wiki/modules/bridge-server.md`：/status 说明同步
- [√] 3.3 更新 `helloagents/wiki/modules/winui-client.md`：按钮“上下文用量”说明同步
- [√] 3.4 更新 `helloagents/CHANGELOG.md` 与 `helloagents/history/index.md`

## 4. 质量验证
- [√] 4.1 构建：`dotnet build codex-bridge-server/codex-bridge-server.csproj -c Release`
- [√] 4.2 构建：`dotnet build codex-bridge/codex-bridge.csproj -c Release -p:Platform=x64`

## 5. 安全检查
- [√] 5.1 确认 `/status` 输出不包含提示词/对话内容/任何密钥，仅限额与用量统计
