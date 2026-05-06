# VeraMedia

VeraMedia 是一个自媒体运营 Agent MVP：用户通过实时对话提交链接、文章或选题，系统保存会话，并通过 OpenAI 兼容接口生成内容。

## 目录结构

```text
VeraMedia/
  src/
    backend/       ASP.NET Core 8 Web API
    frontend/      React + TypeScript + Vite
  README.md
  VeraMedia.sln
```

## 技术栈

- Frontend: React + TypeScript + Vite
- Backend: ASP.NET Core 8 Web API
- Database: MySQL 5.7 via EF Core + Pomelo
- Auth: JWT
- AI: OpenAI-compatible `/v1/chat/completions` streaming

## 本地启动

启动后端：

```powershell
dotnet run --project .\src\backend\VeraMedia.Api.csproj --launch-profile http
```

启动前端：

```powershell
cd .\src\frontend
npm install
npm run dev
```

访问：

- Frontend: http://localhost:5173
- Backend: http://localhost:5178
- Swagger: http://localhost:5178/swagger

## MySQL 5.7 配置

开发时如果 `ConnectionStrings:Default` 为空，后端会使用内存数据库，方便先体验功能。

接 MySQL 时修改 `src/backend/appsettings.json`：

```json
{
  "ConnectionStrings": {
    "Default": "Server=localhost;Port=3306;Database=veramedia;User=root;Password=your_password;CharSet=utf8mb4;"
  }
}
```

首次启动会通过 `EnsureCreated()` 自动创建表。正式环境建议后续切换为 EF Core migrations。

## 默认登录账号

默认账号从 `src/backend/appsettings.json` 或 `src/backend/appsettings.Development.json` 的 `SeedUser` 读取：

```json
{
  "SeedUser": {
    "Enabled": true,
    "Email": "demo@veramedia.local",
    "Password": "12345678",
    "DisplayName": "运营同学"
  }
}
```

后端启动时如果用户不存在，会自动创建该账号。前端登录页也会通过 `/api/app/config` 读取这组默认值来填充表单。

## OpenAI 兼容 Provider

登录后在右侧 API 配置中填写：

- Base URL: `https://api.openai.com/v1`
- API Key: 你的服务端 key
- 聊天模型: 兼容 Chat Completions 的模型名

如果不配置 Provider，系统会返回演示流式回复，便于验证对话体验。

## 已实现的 MVP 能力

- 用户注册、登录、JWT 鉴权
- 会话创建、会话列表、消息持久化
- SSE 实时流式对话
- OpenAI-compatible Provider 保存
- 没有 API Key 时的演示 Agent 回复
- 前端聊天工作台和 API 配置面板

## 下一步建议

- 增加 URL 正文抓取工具
- 增加结构化文章产物表和右侧编辑器
- 增加图片生成接口和图片素材库
- 增加 Refresh Token、API Key 加密、用量统计
