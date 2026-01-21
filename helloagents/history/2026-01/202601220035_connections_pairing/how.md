# 技术设计: 连接（局域网设备配对与多端同步）

## 技术方案

### 核心技术
- **Bridge Server:** .NET 8 / ASP.NET Core（HTTP + WebSocket）
- **WinUI Client:** .NET 8 / WinUI 3（Windows App SDK）
- **Android Client:** Kotlin / Jetpack Compose
- **二维码:**
  - WinUI 端生成：优先采用纯托管库生成 PNG（如 `QRCoder`）；渲染为 `BitmapImage`
  - Android 扫码：优先采用 `ML Kit Barcode Scanning`（或备选 `ZXing`）

### 实现要点
1. **设备级授权（Device Token）**
   - 每台设备首次配对成功后获得独立 Bearer Token（仅首次下发一次）。
   - 服务端仅持久化 token 哈希（如 SHA-256/HMAC-SHA256），不保存明文；支持逐设备撤销。
2. **显式启用远程 + 配对邀请码**
   - 远程访问默认关闭；仅在 WinUI 用户显式启用“允许局域网连接”后对外监听。
   - 配对基于短时有效邀请码（二维码包含），并且仍需 WinUI 端确认，降低局域网内骚扰与误配对风险。
3. **同步复用既有协议**
   - 会话列表/历史消息/plan：复用现有 HTTP API。
   - 流式输出/plan 更新：复用现有 WS 事件（`chat.message.delta` / `run.plan.updated` 等）。
   - 仅补充连接/配对/设备在线状态相关的新增 API 与 WS 事件。

## 架构设计

```mermaid
flowchart LR
    Win[WinUI Client] <-->|HTTP/WS(Loopback)| Svc[Bridge Server]
    And[Android Client] <-->|HTTP/WS(LAN + Bearer Device Token)| Svc

    subgraph Pairing[配对流程]
        UI[WinUI 生成邀请码/二维码] --> QR[二维码: baseUrl + pairingCode]
        QR --> Scan[Android 扫码]
        Scan --> Claim[HTTP: pairing claim]
        Claim --> Req[WS: device.pairing.requested -> WinUI]
        Req --> Approve[WinUI 确认/拒绝]
        Approve --> Token[HTTP: pairing result -> deviceToken]
    end
```

## 架构决策 ADR

### ADR-005: 采用“邀请码 + PC 确认”的设备级配对（推荐）
**上下文:** 需要在局域网内让 Android 访问 Bridge Server，同时要求首次连接需确认，并支持撤销某设备。  
**决策:** WinUI 生成短时有效的 `pairingCode`（二维码携带），Android 使用该邀请码发起配对请求；Bridge Server 通过现有 WS 通知 WinUI 弹窗确认；确认后服务端签发 `deviceToken`（设备级 Bearer Token），并支持撤销。  
**理由:** 满足“默认安全 + 易用”的平衡；避免单一全局 token 带来的不可治理；邀请码降低配对骚扰与误配对概率。  
**替代方案:**  
- 全局 BearerToken：实现简单，但无法逐设备撤销、首次确认弱（不符合需求）。  
- 公钥/证书绑定（mTLS/签名）：安全性强但实现成本高、移动端/Windows 端复杂度上升（不作为 MVP）。  
**影响:** 需要新增配对与设备存储模块，并扩展鉴权逻辑与 WS 事件。

### ADR-006: 远程监听采用“WinUI 显式启用后重启后端切换监听地址”（推荐）
**上下文:** Kestrel 监听地址通常在启动时确定；同时项目安全默认要求“远程默认关闭”。  
**决策:** 后端默认仅监听 `127.0.0.1`；用户在“连接”页开启局域网共享时，由 WinUI 重启后端并改为监听 `0.0.0.0`（同时保持回环可用）。关闭共享同样重启恢复仅回环。  
**理由:** 避免“未启用也对外监听”的安全边界模糊；只在用户显式操作时触发 Windows 防火墙提示。  
**替代方案:** 永久监听 `0.0.0.0` 但在应用层拒绝远程：减少重启，但扩大暴露面且可能让用户误以为远程未开启。  
**影响:** WinUI 需要具备重启后端与重连能力，并在开启/关闭时提示可能短暂断连。

### ADR-007: 服务端仅存储 token 哈希并支持强制断开
**上下文:** Bearer token 泄露会导致越权访问；明文落盘风险较高。  
**决策:** 服务端落盘存储 token 哈希（带盐或 HMAC），校验时对请求 token 计算哈希并常量时间比较；撤销设备时同步关闭其 WS 连接并标记为 revoked。  
**理由:** 降低磁盘泄露风险；撤销即时生效。  
**替代方案:** 明文存储：实现最简单但安全性弱。  
**影响:** 需要设备存储结构升级与哈希工具函数、以及 WS 客户端到设备的映射表。

## API 设计

### HTTP（新增）
> 说明：以下路径为建议；实现时可按现有 `api/v1/*` 风格调整。

#### [POST] /api/v1/connections/pairings
- **用途:** 创建短时配对邀请码（WinUI 本机调用，回环可免 token）
- **请求:** `{ "expiresInSeconds": 300 } (optional)`
- **响应:** `{ "pairingCode": "...", "expiresAt": "..." }`

#### [POST] /api/v1/connections/pairings/claim
- **用途:** Android 使用 `pairingCode` 发起配对请求（远程允许，无需 Bearer）
- **请求:** `{ "pairingCode": "...", "deviceName": "...", "platform": "android", "deviceModel": "...", "appVersion": "..." }`
- **响应:** `{ "requestId": "...", "pollAfterMs": 800 }`

#### [GET] /api/v1/connections/pairings/{requestId}
- **用途:** Android 轮询配对结果
- **响应:**
  - pending：`{ "status": "pending" }`
  - approved：`{ "status": "approved", "deviceId": "...", "deviceToken": "..." }`
  - declined/expired：`{ "status": "declined|expired" }`

#### [POST] /api/v1/connections/pairings/{requestId}/respond
- **用途:** WinUI 对配对请求进行确认/拒绝（仅回环可用）
- **请求:** `{ "decision": "approve|decline" }`
- **响应:** `{ "status": "approved|declined|expired", "deviceId": "..."(approved时) }`

#### [GET] /api/v1/connections/devices
- **用途:** 获取已配对设备列表（WinUI 本机调用）
- **响应:** `[{ "deviceId": "...", "name": "...", "platform": "...", "createdAt": "...", "lastSeenAt": "...", "revoked": false, "online": true }]`

#### [DELETE] /api/v1/connections/devices/{deviceId}
- **用途:** 撤销设备（WinUI 本机调用）
- **响应:** `{ "deviceId": "...", "revoked": true }`

### WebSocket（新增/扩展）

#### event device.pairing.requested
WinUI 收到远程设备的配对请求并弹窗确认。
`{ "requestId": "...", "deviceName": "...", "platform": "...", "deviceModel": "...", "clientIp": "..." }`

#### event device.presence.updated（可选）
用于 UI 显示在线状态变化（连接/断开/心跳）。
`{ "deviceId": "...", "online": true, "lastSeenAt": "..." }`

## 数据模型

### 已配对设备（服务端本地存储，JSON）
```json
{
  "version": 1,
  "devices": [
    {
      "deviceId": "uuid",
      "name": "Pixel 8",
      "platform": "android",
      "deviceModel": "google/pixel8",
      "createdAt": "2026-01-22T00:00:00Z",
      "lastSeenAt": "2026-01-22T00:10:00Z",
      "revokedAt": null,
      "tokenHash": "base64..."
    }
  ]
}
```

## 安全与性能
- **安全:**
  - 远程访问默认关闭；开启/关闭由 WinUI 显式触发并提示防火墙弹窗可能出现。
  - 配对邀请码短时有效、一次性/可重放限制；配对请求限速（按 IP/邀请码）。
  - 设备 token 不明文落盘；撤销后立即关闭 WS 并拒绝后续请求。
  - 远程端仅允许访问受控 API（会话/消息/plan/WS 事件），管理类接口默认仍建议仅回环可用。
- **性能:**
  - 设备鉴权哈希校验为常量时间 O(1)；设备列表与在线状态由内存索引维护并按需落盘。
  - WS 广播增加 presence 事件时需避免过高频率（心跳可选或降频）。

## 测试与部署
- **测试:**
  - 后端单测：鉴权（回环/远程/撤销）、配对邀请码生命周期、设备存储读写与并发。
  - 集成测试：配对 claim → WS 通知 → respond → poll 得到 token → 远程访问 sessions/messages/plan。
- **部署/运行:**
  - WinUI 默认启动后端仅回环；在“连接”页启用共享会触发后端重启为 `0.0.0.0` 监听。
  - Android 需同一局域网；扫码获取 baseUrl 后完成配对并持久保存 deviceToken。
