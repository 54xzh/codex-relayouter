# 任务清单: 会话进入后自动定位到底部

目录: `helloagents/plan/202601191115_chat_scroll_bottom/`

---

## 1. WinUI Client
- [√] 1.1 在 `codex-bridge/Pages/ChatPage.xaml.cs` 中实现历史加载完成后的滚动到底部逻辑，验证 why.md#需求-进入会话自动定位到底部-场景-点击会话进入聊天页
- [√] 1.2 处理布局时序问题：在 UI 线程调度后再执行 `ScrollIntoView`（必要时 `UpdateLayout`），并确保空会话不抛异常

## 2. 安全检查
- [√] 2.1 执行安全检查（按G9: 输入验证、敏感信息处理、权限控制、EHRB风险规避）

## 3. 文档更新
- [√] 3.1 更新 `helloagents/wiki/modules/winui-client.md`，补充“进入会话自动定位到底部”的交互约定

## 4. 测试
- [?] 4.1 手动验证：切换会话进入后自动定位到底部；实时消息追加仍保持自动滚动
  > 备注: 需要在本机启动 WinUI 应用后手动确认（本次自动化流程仅完成 `dotnet build`）。
