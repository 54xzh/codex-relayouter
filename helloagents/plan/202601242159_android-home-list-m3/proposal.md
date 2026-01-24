# Android 会话列表（主页）UI：Material3 列表规范重制

## 背景
当前 Android 会话列表以 Card + 自定义 Row 实现，列表规范（间距、分隔、尾随状态呈现）在不同设备上不够一致；同时 WebSocket 连接状态缺少直观展示。

## 目标
- 会话列表项改为 Material3 `ListItem` 结构（leading/headline/supporting/trailing），并使用分隔线形成标准列表观感。
- 运行状态指示在 trailing 统一呈现：Running/Completed/Warning/None（None 显示导航箭头）。
- 连接状态卡补充显示 WS 状态文本。
- 空列表状态提供显式 CTA（“新建会话”按钮），与 FAB 互补。

## 范围
### 范围内
- Android：`SessionListScreen` 列表 UI 重构（ListItem + divider + 空态 CTA）
- Android：连接状态卡 UI 补充 wsStatus 展示
- 测试：确保 `:app:compileDebugKotlin` / `:app:testDebugUnitTest` 通过
- 文档：更新 Android Client 模块文档与 CHANGELOG

### 范围外
- 会话列表数据/协议改动
- 导航结构与主题系统调整

## 方案与关键决策
- 列表项使用 `ListItem` slots：
  - leading：圆形容器 + `ChatBubbleOutline`
  - headline：会话标题
  - supporting：会话 id（monospace）
  - trailing：根据 `SessionIndicatorKind` 显示 `CircularProgressIndicator` / `CheckCircle` / `ErrorOutline` / `ChevronRight`
- 列表分隔：`HorizontalDivider`，并对齐内容左边距做缩进。
- 布局：使用 `Column` + `LazyColumn(weight=1f)`，避免固定 header 导致列表测量溢出；空态/加载态在剩余区域居中。

## 验收标准
- 会话列表项符合 Material3 ListItem 布局（两行文本 + leading/trailing）。
- 状态指示与导航提示清晰，且不影响点击进入会话。
- Android 构建通过：`./gradlew :app:compileDebugKotlin`，单测通过：`./gradlew :app:testDebugUnitTest`。

