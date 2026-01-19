# 任务清单: 修复图片发送导致 codex 执行失败

目录: `helloagents/plan/202601191827_chat_image_failure_fix/`

---

## 1. 复现与根因定位
- [√] 1.1 复现：发送图片后出现 `run.failed: codex 执行失败`
- [√] 1.2 定位：确认是否由图片格式/编码不被 codex 接受（重点排查 BMP/剪贴板 bitmap）

## 2. WinUI Client（图片编码兼容）
- [√] 2.1 对 BMP/剪贴板图片做兼容：在生成 data URL 时转为 `image/png`（避免发送 `image/bmp` 导致失败）
- [√] 2.2 保持大小限制与数量限制不变（单张 ≤10MB、单条 ≤4 张）

## 3. Bridge Server（失败信息透出）
- [√] 3.1 当 `turn.status=failed` 时，从 `turn.error.message` 提取原因并透出到 `run.failed.message`（替代固定“codex 执行失败”）

## 4. 文档与验证
- [√] 4.1 更新 `helloagents/CHANGELOG.md` 记录修复点
- [√] 4.2 `dotnet build` 验证 client/server 编译通过

## 5. 历史迁移
- [√] 5.1 迁移方案包至 `helloagents/history/2026-01/` 并更新 `helloagents/history/index.md`
