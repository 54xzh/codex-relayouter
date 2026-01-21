# 任务清单: 连接（局域网设备配对与多端同步）

目录: `helloagents/plan/202601220035_connections_pairing/`

---

## 1. Bridge Server（配对/设备存储/鉴权/事件）
- [√] 1.1 新增设备数据模型与本地持久化（JSON 文件）：支持 deviceId/name/platform/model/createdAt/lastSeenAt/revokedAt/tokenHash；验证 why.md#需求-撤销设备与安全策略-场景-winui-撤销某设备后立即失效
- [√] 1.2 新增配对邀请码与请求流转服务：创建 `pairingCode`（短时有效）、接收 claim、生成 pending request，并提供查询结果；验证 why.md#需求-局域网配对扫码--pc-确认-场景-android-扫码发起配对-winui-确认后建立连接
- [√] 1.3 扩展 `BridgeRequestAuthorizer`：远程启用时改为校验“设备 token”；保留回环免 token；为配对 claim/poll 端点放行但要求 `pairingCode/requestId`；验证 why.md#需求-局域网配对扫码--pc-确认-场景-android-扫码发起配对-winui-确认后建立连接
- [√] 1.4 新增 `ConnectionsController`：pairings(devices) 相关 HTTP API（创建邀请码/claim/poll/列出设备/撤销设备/确认）；验证 why.md#需求-连接入口与设备管理-场景-在-winui-侧边栏底部进入连接页
- [√] 1.5 扩展 `WebSocketHub`：新增 `device.pairing.requested` 事件与 `device.presence.updated` 在线状态；为远程 WS 连接关联 deviceId，撤销时主动断开；验证 why.md#需求-连接入口与设备管理-场景-在-winui-侧边栏底部进入连接页
  > 备注: 配对“确认/拒绝”采用 HTTP `POST /api/v1/connections/pairings/{requestId}/respond`（未新增 WS command `device.pairing.respond`）。

## 2. WinUI Client（连接页/二维码/审批/后端共享开关）
- [√] 2.1 在 `codex-bridge/MainWindow.xaml(.cs)` 底部导航新增“连接”入口并创建 `ConnectionsPage` 页面骨架；验证 why.md#需求-连接入口与设备管理-场景-在-winui-侧边栏底部进入连接页
- [√] 2.2 `ConnectionsPage` 展示后端状态与局域网地址（网卡/IP 列表），并生成二维码（baseUrl + pairingCode）；验证 why.md#需求-局域网配对扫码--pc-确认-场景-android-扫码发起配对-winui-确认后建立连接
- [√] 2.3 `ConnectionsPage` 获取并展示已配对设备列表（在线/离线、最近活动），支持撤销；验证 why.md#需求-撤销设备与安全策略-场景-winui-撤销某设备后立即失效
- [√] 2.4 WinUI 处理 `device.pairing.requested`：弹窗确认/拒绝并回传确认结果；验证 why.md#需求-局域网配对扫码--pc-确认-场景-android-扫码发起配对-winui-确认后建立连接
  > 备注: 确认结果通过 HTTP `POST /api/v1/connections/pairings/{requestId}/respond` 提交。
- [√] 2.5 按 ADR-006 实现“允许局域网连接”开关：重启后端切换监听地址（回环 ↔ 0.0.0.0）并自动重连；验证 why.md#需求-连接入口与设备管理-场景-在-winui-侧边栏底部进入连接页

## 3. Android Client（扫码/配对/HTTP+WS 同步）
- [√] 3.1 增加网络层与协议模型（HTTP sessions/messages/plan + WS envelope/事件），并支持设置 Authorization: Bearer deviceToken；验证 why.md#需求-多端同步会话消息流式输出plan设置-场景-android-查看会话与流式输出并可发送消息
- [-] 3.2 实现扫码页面：解析二维码内容（baseUrl/wsUrl/pairingCode），并展示配对状态；验证 why.md#需求-局域网配对扫码--pc-确认-场景-android-扫码发起配对-winui-确认后建立连接
  > 备注: MVP 采用“粘贴二维码内容/手动输入 baseUrl + pairingCode”完成配对；未集成相机扫码。
- [√] 3.3 实现配对流程：claim + poll 获取 deviceToken，安全存储（Android Keystore + AES-GCM），并自动建立 WS 连接；验证 why.md#需求-局域网配对扫码--pc-确认-场景-android-扫码发起配对-winui-确认后建立连接
- [√] 3.4 会话列表与历史消息：调用现有 sessions API 并渲染；验证 why.md#需求-多端同步会话消息流式输出plan设置-场景-android-查看会话与流式输出并可发送消息
- [√] 3.5 聊天与流式输出：发送 `chat.send`，处理 `chat.message`/`chat.message.delta` 与 `run.plan.updated`；验证 why.md#需求-多端同步会话消息流式输出plan设置-场景-android-查看会话与流式输出并可发送消息

## 4. 安全检查
- [√] 4.1 确认远程监听默认关闭、启用需显式操作；配对邀请码短时有效；撤销设备立即断开 WS 并拒绝后续访问（401/close）
- [√] 4.2 确认服务端不落盘明文 deviceToken（仅哈希），并避免在日志中输出令牌

## 5. 文档与协议同步
- [√] 5.1 更新 `helloagents/wiki/api.md`：补充 connections/pairings/devices 相关接口与鉴权说明
- [√] 5.2 更新 `helloagents/wiki/modules/protocol.md`：补充 `device.pairing.requested` 与 `device.presence.updated`
- [√] 5.3 更新 `helloagents/wiki/modules/bridge-server.md` 与 `helloagents/wiki/modules/winui-client.md`：补充连接页/配对/撤销与远程监听策略说明
- [√] 5.4 更新 `helloagents/CHANGELOG.md`：记录新增“连接”功能与接口/事件

## 6. 测试
- [√] 6.1 在 `codex-bridge-server.Tests` 增加鉴权与配对流程测试（回环/远程/撤销/过期/未授权）
- [X] 6.2 基本联调：WinUI 生成二维码 → Android 扫码配对 → 双端会话/流式/plan 同步；记录发现的问题与回归点
  > 备注: 当前环境 Gradle 无法从 `dl.google.com` 拉取依赖（TLS handshake 失败），导致 Android 构建/联调无法完成；服务端测试已通过，WinUI/服务端已可编译。
