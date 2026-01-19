# 任务清单: 上下文用量 Flyout 与限额进度条

目录: `helloagents/plan/202601192243_context_usage_flyout/`

---

## 1. Bridge Server
- [√] 1.1 在 `codex-bridge-server/Bridge/StatusTextBuilder.cs` 中将限额“重置时间”格式调整为 `MM-dd HH:mm`（不显示时区）

## 2. WinUI Client
- [√] 2.1 在 `codex-bridge/Pages/ChatPage.xaml.cs` 中将按钮点击展示改为 `Flyout`（替代 `ContentDialog`）
- [√] 2.2 在 Flyout 中为 5h/周限额增加进度条展示，并从 `/status` 文本解析百分比与重置时间
- [√] 2.3 确认 Flyout/按钮使用全局默认字体（不显式设置 `FontFamily`）

## 3. 文档更新
- [√] 3.1 更新 `helloagents/wiki/modules/winui-client.md`（弹窗 → Flyout；限额进度条）
- [√] 3.2 更新 `helloagents/CHANGELOG.md` 与 `helloagents/history/index.md`

## 4. 质量验证
- [√] 4.1 构建：`dotnet build codex-bridge-server/codex-bridge-server.csproj -c Release`
- [√] 4.2 构建：`dotnet build codex-bridge/codex-bridge.csproj -c Release -p:Platform=x64`

## 5. 安全检查
- [√] 5.1 检查 Flyout 仅展示统计信息；/status 输出不含敏感数据
