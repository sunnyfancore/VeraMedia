# VeraMedia

VeraMedia 是一个自媒体运营 Agent 工作台：用户通过实时对话提交链接、文章、附件或选题，系统保存会话，并通过 OpenAI 兼容接口完成写作、配图、分析、翻译、研究和 PPT 生成等任务。

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
- 聊天模型: 兼容 Chat Completions 或 Responses 的模型名
- 生图模型: 兼容 `/v1/images/generations` 的模型名

如果不配置 Provider，系统会返回演示流式回复，便于验证对话体验。

## 已实现能力

- 用户注册、登录、JWT 鉴权
- 会话创建、会话列表、消息持久化
- 后台生成任务、任务中心、取消/重试和 SSE 进度同步
- OpenAI-compatible Provider 保存、模型获取、聊天/生图测试
- GPT-5.5 风格思考层级：快速、思考、专家
- 写作、图像生成、编程、翻译、深入研究、解题答疑、数据分析、超能模式、PPT 生成
- 上传附件预览、格式限制、附件正文提取
- 支持图片题图/参考图以多模态方式发送给模型
- URL 正文抓取和参考来源浮层
- 文章资产、图片资产、版本恢复、文档编辑器
- DOCX/PPTX 导出
- Linux Docker 下 PPT 转视频，支持读取每页备注生成本地 TTS 音频并同步合成 MP4

## Linux Docker 部署

先发布后端，发布产物会包含前端构建文件和 PPT 转视频脚本：

```bash
dotnet publish src/backend/VeraMedia.Api.csproj -c Release -o publish
```

然后构建并启动容器：

```bash
docker compose build --no-cache
docker compose up -d
```

访问默认端口：

- Web: `http://服务器IP:5178`
- 容器内服务端口: `8080`

持久化目录：

- `./AppData:/app/AppData`
- `./publish:/app`

生产环境需要配置可用的 MySQL 5.7 连接串。可以通过环境变量覆盖：

```text
ConnectionStrings__Default=Server=你的MySQL地址;Port=3306;Database=veramedia;User=用户名;Password=密码;CharSet=utf8mb4;SslMode=None;
```

注意：容器内的 `localhost` 指容器自身。如果 MySQL 在宿主机或其他容器中，请使用宿主机可访问地址、Docker 网络服务名，或反向代理/数据库内网地址，不要直接使用 `localhost`。

Docker 镜像内已安装 PPT 转视频所需依赖：

- `libreoffice`
- `poppler-utils`
- `ffmpeg`
- `espeak-ng`
- `fonts-noto-cjk`

如果放在 Nginx 或其他 HTTPS 反向代理后面，请转发以下头，后端会用它们生成正确的上传文件 URL：

```nginx
proxy_set_header Host $host;
proxy_set_header X-Forwarded-Host $host;
proxy_set_header X-Forwarded-Proto $scheme;
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
```

## 重新部署

```bash
git pull
dotnet publish src/backend/VeraMedia.Api.csproj -c Release -o publish
docker compose build --no-cache
docker compose up -d
docker compose logs -f veramedia
```
