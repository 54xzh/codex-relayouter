# 任务清单：Android 聊天 Trace 展示

- [√] assistant 消息移除气泡背景，正文直出
- [√] `SessionMessage` 支持反序列化 `trace` 并在历史加载中展示
- [√] WS 事件处理补齐：`run.started/run.completed/run.failed/run.canceled`
- [√] WS 事件处理补齐：`run.command/run.command.outputDelta/run.reasoning/run.reasoning.delta/diff.updated`
- [√] Trace UI：整体“执行过程（n）”折叠区块 + 条目展开
- [√] 折叠策略对齐 Windows：history 默认折叠；latest reasoning 自动展开；diff 默认展开
- [√] 新增单元测试覆盖 Trace 解析与折叠策略
- [√] 更新知识库文档与 CHANGELOG
