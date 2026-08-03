# CherryKey

> 当前版本：v0.4.0（全新三栏 UI + 亮/暗主题 + v1/v2 双数据源兼容版）

CherryKey 是 Cherry Studio 的 Windows 本地 API 配置伴生工具。它只读 Cherry Studio 的本地配置，提供供应商搜索、API Key / Base URL / 模型 ID 快速复制，以及常见客户端配置模板。

## v0.4.0 更新重点

- 按 `CherryKey_UI_Refactor_v0.4` 设计稿重建 WPF 主界面
- 使用更紧凑的 292 / 自适应 / 276 三栏布局与统一卡片层级
- 新增可即时切换的亮色、暗色主题
- 重构供应商列表、数据源状态、详情标签页和快捷操作区
- 保留 API Key 遮盖、配置复制、导出、托盘及 v1/v2 自动发现能力

## v0.3.2 解析修复

- 正确解析 Chromium Local Storage 的 `_<origin>\0<script key>` 二进制键结构
- 正确处理 `0x00 = UTF-16LE`、`0x01 = Latin-1`，不再把 Latin-1 当成 UTF-8
- 优先读取 `persist:cherry-studio`，兼容 `persist:root` 与其他 `persist:*`
- 每个候选都实际验证 `llm.providers`，不会再被错误 fallback 抢占
- 自动检查 Electron `Partitions\*\Local Storage\leveldb`
- 日志仅记录候选键名、编码和数量，不记录 JSON 内容或 API Key
- 保留 v0.3.1 的后台扫描、限时读取、错误候选跳过和真实启动测试

## v1/v2 兼容范围

| Cherry Studio | 数据源 | Windows 默认位置 |
|---|---|---|
| v1.x | Chromium Local Storage LevelDB | `%APPDATA%\CherryStudio\Local Storage\leveldb` |
| v2.x | SQLite | `%APPDATA%\CherryStudio\Data\cherrystudio.sqlite` |
| 便携版/迁移目录 | 自动识别上述两种结构 | `<自定义 userData>` 或 `<EXE目录>\data` |

程序启动时会自动检查上一次路径、Roaming/Local AppData、便携目录、迁移配置和正在运行的 Cherry Studio。若 v1 与 v2 数据同时存在，优先读取 v2 SQLite。

## 当前功能

- 自动识别 Cherry Studio v1 LevelDB 与 v2 SQLite
- v1 读取 `persist:cherry-studio` 中的 `llm.providers`
- v2 只读读取 `user_provider`、`user_model`
- Cherry Studio 运行中也可读取：v1 先创建临时只读快照，不碰 `LOCK`
- 支持供应商、模型和 Provider ID 搜索
- API Key 默认遮盖，可临时显示和复制
- 生成 Claude Code、OpenAI、Gemini CLI、Codex TOML
- JSON / Markdown 导出与自定义模板
- 数据源变化后自动刷新
- Windows 托盘与 `Ctrl + Shift + K` 全局快捷键
- 复制敏感内容 30 秒后自动清理（仅当剪贴板仍是原内容）

## 手动选择数据源

- v2：选择 `cherrystudio.sqlite`
- v1：进入 `Local Storage\leveldb`，选择 `CURRENT`、任意 `.ldb` 或 `.log` 文件；CherryKey 会自动归一化为 LevelDB 目录

## 安全边界

- SQLite 使用 `Mode=ReadOnly`
- v1 LevelDB 只读取临时快照，不写原目录
- 不创建第二份持久化 API Key 数据库
- 不写回、不迁移 Cherry Studio 数据
- 不联网、不上传，日志不记录 API Key
- 设置文件仅保存数据源路径

## 构建

需要 Windows 和 .NET 10 SDK：

```powershell
dotnet restore CherryKey.sln
dotnet build CherryKey.sln -c Release
dotnet publish src/CherryKey/CherryKey.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false
```

GitHub Actions 同时生成稳定多文件版与单文件版。由于 LevelDB 包含原生库，建议优先使用 `CherryKey-portable-win-x64`。

## 已知限制

1. v1 兼容层针对官方 Redux Persist 的 `persist:cherry-studio` / `llm.providers` 结构；非常旧或第三方修改版可能需要单独适配。
2. 预设供应商未保存的默认 Base URL 仍可能显示为“由 Cherry 预设继承或未保存”。
3. AWS、Vertex 等非普通 API Key 认证仅展示能从供应商状态中安全识别的字段。
