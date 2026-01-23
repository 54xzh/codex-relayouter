# Android Client

## 目的
提供 Android 端远程入口，通过局域网配对连接 Bridge Server，实现会话列表与聊天控制能力。

## 模块概述
- **职责:**
  - 连接设备（局域网配对，获取并保存 deviceToken）
  - 会话列表（加载会话、进入聊天）
  - 聊天界面（加载历史、连接 WS、发送 `chat.send`、展示 plan 与执行过程/trace）
- **状态:** ??开发中
- **最后更新:** 2026-01-23

## 规范

### 需求: Android UI 骨架（三模块）
**模块:** Android Client  
Android 端 UI 必须拆分为三个模块页面，并统一使用 Material3。

#### 场景: 首次启动配对后进入会话列表
首次启动或本地无有效配对信息时
- 进入“连接设备”页面
- 完成配对后保存 deviceToken，并跳转到“会话列表”

#### 场景: 从会话列表进入聊天
已完成配对
- 进入“会话列表”作为主界面
- 点击任一会话，进入“聊天界面”并加载历史消息
  - assistant 消息支持展示“执行过程（Trace）”：思考摘要（run.reasoning）与执行命令（run.command）

#### 场景: 从主界面进入连接设备
已完成配对但需要重新配对或切换后端
- 从“会话列表”进入“连接设备”
- 可返回会话列表或完成新配对后回到会话列表

## API接口

### [POST] /api/v1/connections/pairings/claim
**描述:** 提交 pairingCode 并创建配对请求（Android 侧 claim）  
**输入:** pairingCode/deviceName/platform...  
**输出:** requestId/pollAfterMs

### [GET] /api/v1/connections/pairings/{requestId}
**描述:** 轮询配对状态并在 approved 时返回 deviceToken/deviceId  
**输入:** requestId  
**输出:** status/deviceToken/deviceId

### [GET] /api/v1/sessions
**描述:** 获取会话列表（需要 Bearer deviceToken）  
**输入:** limit  
**输出:** SessionSummary[]

### [GET] /api/v1/sessions/{sessionId}/messages
**描述:** 获取会话历史消息（需要 Bearer deviceToken）  
**输入:** limit  
**输出:** SessionMessage[]（assistant 消息可包含 trace：reasoning/command）

### [GET] /api/v1/sessions/{sessionId}/plan
**描述:** 获取会话最新计划（turn plan）（需要 Bearer deviceToken）  
**输入:** sessionId  
**输出:** TurnPlanSnapshot

### [WS] /ws
**描述:** 连接 WebSocket 后发送 `chat.send`，接收 `chat.message` / `chat.message.delta` / `run.plan.updated` 等事件  
**输入:** Authorization: Bearer deviceToken  
**输出:** 事件流（JSON Envelope）

## UI 约定（聊天）
- assistant 正文不使用气泡背景（文字直接显示在页面背景上）
- 执行过程（Trace）以可折叠区块展示，折叠策略与 Windows 端保持一致：
  - 历史消息默认折叠
  - 运行中可随事件增量更新（reasoning/command/diff），必要时自动展开

## 数据模型

### ConnectionConfig
| 字段 | 类型 | 说明 |
|------|------|------|
| baseUrl | string | Bridge Server 基地址（含协议与尾随 `/`） |
| deviceToken | string | 设备令牌（本地加密保存） |
| deviceId | string | 设备标识（后端返回，可空） |

## 依赖
- Bridge Server
- Protocol

## 变更历史
- [202601220211_android_material3_skeleton](../../history/2026-01/202601220211_android_material3_skeleton/) - Android：Material3 三模块骨架（会话/聊天/连接设备）

