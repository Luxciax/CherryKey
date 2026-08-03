# CherryKey

> 当前界面版本：v0.2.0（概念图复原版）

CherryKey 是 Cherry Studio 的 Windows 本地 API 配置伴生工具。

它不会重新实现模型协议适配，也不会修改 Cherry Studio 数据。它只读 Cherry Studio 的 SQLite 数据库，提供供应商搜索、API Key / Base URL / 模型 ID 快速复制，以及常见客户端配置模板。

## 当前功能

- 自动发现默认目录、迁移配置、便携目录和正在运行的 Cherry Studio 数据库
- 支持手动选择自定义数据目录中的数据库
- 只读读取 `user_provider`、`user_model`
- 兼容 API Key JSON 数组和常见旧格式
- 搜索供应商、模型、Provider ID
- API Key 默认遮盖，可临时显示
- 一键复制 Key、Base URL、模型 ID、完整信息
- 生成 Claude Code、OpenAI、Gemini CLI、Codex TOML
- 自定义 `{{变量}}` 复制模板
- 导出单个供应商为 JSON / Markdown
- 数据库及 WAL 文件变化后自动刷新
- Windows 托盘与 `Ctrl + Shift + K` 全局快捷键
- 复制敏感内容后 30 秒自动清理剪贴板（仅在剪贴板仍是原内容时）

## 安全边界

- SQLite 使用 `Mode=ReadOnly`
- 不创建第二份 API Key 数据库
- 不写回、不修复、不迁移 Cherry Studio 数据
- 不联网、不上传、日志中不记录 API Key
- 设置文件只保存用户手动选择的数据库路径

## 已知限制

1. Cherry Studio 预设供应商的默认 Base URL 来自其 Provider Registry。若用户没有在数据库中覆盖 Base URL，第一版只显示“由 Cherry 预设继承或未保存”，不会读取应用安装包中的 Registry。
2. 第一版针对 Cherry Studio V2 SQLite 数据结构；旧版 IndexedDB / LevelDB 不在支持范围。
3. Codex TOML 只是配置文本生成器，不保证某个上游协议一定被 Codex 原生支持。
4. 程序处于只读模式，添加或修改供应商仍需在 Cherry Studio 完成。

## 构建

需要 Windows 和 .NET 10 SDK：

```powershell
dotnet restore CherryKey.sln
dotnet build CherryKey.sln -c Release
dotnet publish src/CherryKey/CherryKey.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

GitHub Actions 会在每次推送 `main` 后构建自包含 `win-x64` ZIP；推送 `v*` 标签会自动创建 Release。

## 自定义模板变量

```text
{{providerId}}
{{providerName}}
{{presetProviderId}}
{{protocol}}
{{endpointType}}
{{baseUrl}}
{{apiKey}}
{{apiKeyLabel}}
{{modelId}}
{{modelName}}
{{authType}}
{{authHeader}}
```
