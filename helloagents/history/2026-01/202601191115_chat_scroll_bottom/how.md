# 技术设计: 会话进入后自动定位到底部

## 技术方案

### 核心技术
- WinUI 3 `ListView.ScrollIntoView`
- UI 线程调度：`DispatcherQueue.TryEnqueue`

### 实现要点
- 在 `ChatPage.LoadSessionHistoryIfNeededAsync()` 完成历史消息填充后，触发一次“滚动到底部”。
- 将滚动逻辑封装为 `ScrollMessagesToBottom()`（或同等语义）辅助方法：
  - 若 `Messages.Count == 0` 直接返回。
  - 使用 UI 线程调度执行滚动，并在滚动前执行一次 `MessagesListView.UpdateLayout()`（或等价策略）以提高成功率。
- 不改变现有实时事件处理中的自动滚动逻辑（目前在处理 envelope 后会滚动到最后一条）。

## 安全与性能
- **安全:** 无新增外部输入与权限变更。
- **性能:** 仅在“进入会话并加载历史”时追加一次滚动；避免在循环内反复滚动导致卡顿。

## 测试与部署
- **测试:** 手动验证
  - 进入包含大量历史消息的会话，确认加载完成后自动定位到最后一条。
  - 切换多个会话，确认每次进入均能定位到底部。
  - 新建会话或空会话不应报错。
- **部署:** 无额外步骤。
