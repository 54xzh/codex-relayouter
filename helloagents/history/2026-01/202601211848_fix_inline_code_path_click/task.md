# 任务清单: 修复行内代码路径误判与重复打开（轻量迭代）

目录: `helloagents/plan/202601211848_fix_inline_code_path_click/`

---

## 1. 行内代码点击打开
- [√] 1.1 修复点击行内代码中的文件夹时会打开两次资源管理器的问题（增加 Tap 防抖/事件处理）

## 2. 行内代码路径识别
- [√] 2.1 避免将 `GET /...` / `POST /...` 等 HTTP 路由误识别为文件夹路径（增加请求行识别与排除规则）

## 3. 文档同步
- [√] 3.1 更新 `helloagents/wiki/modules/winui-client.md`（补充行内路径识别规则与点击行为）
- [√] 3.2 更新 `helloagents/CHANGELOG.md`（修复项记录）

## 4. 质量验证
- [-] 4.1 按需求：无需编译/测试验证

## 5. 方案包归档
- [√] 5.1 将方案包迁移至 `helloagents/history/2026-01/` 并更新 `helloagents/history/index.md`
