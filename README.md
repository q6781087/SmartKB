# SmartKB 企业知识库智能问答系统

.NET 10 + MAF + Semantic Kernel + PostgreSQL(pgvector) 的可私有化交付知识库问答产品。

当前进度：**M1–M5 全部完成**（后端全链路 + Vue3 前端 + Docker 交付物 + OpenTelemetry），私有化部署见 `docs/部署文档.md`。

## 项目结构

```
SmartKB.slnx
├─ src/SmartKB.Domain          实体与枚举（无外部依赖）
├─ src/SmartKB.Infrastructure  EF Core + Npgsql + pgvector、迁移、种子
├─ src/SmartKB.Application     业务服务（认证/用户/角色/知识库/授权）
├─ src/SmartKB.AI              AI 层（M2/M3：解析、Embedding、RAG、Agent）
├─ src/SmartKB.Api             WebApi 宿主（JWT、控制器、SignalR 预留）
├─ tests/SmartKB.Tests         xUnit
└─ docs/                       方案设计文档
```

# 本地开发（或直接用 Docker 一键部署，见 docs/部署文档.md）

```bash
# Docker 一键部署（生产/演示）
cp .env.example .env && vi .env && docker compose up -d --build
```

1. 起 PostgreSQL（需内置 pgvector 扩展）：

```bash
docker run -d --name smartkb-pg -p 5432:5432 \
  -e POSTGRES_DB=smartkb -e POSTGRES_USER=smartkb -e POSTGRES_PASSWORD=smartkb \
  pgvector/pgvector:pg17
```

2. 运行 API（首次启动自动执行迁移 + 种子默认管理员）：

```bash
cd src/SmartKB.Api
dotnet run
```

3. 验证：打开 `http://localhost:<port>/openapi/v1.json`，POST `/api/auth/login`：

```json
{ "username": "admin", "password": "SmartKB@2026" }
```

4. 运行前端（Vite dev，代理 `/api` 与 `/hubs` 到 API 的 5219 端口）：

```bash
cd web
npm install
npm run dev
```

打开 `http://localhost:5173` → 登录 → 智能问答 / 管理后台（admin、kb_manager 可见）。
生产部署：`npm run build` 产物在 `web/dist`，由 Nginx 托管并反代 API。

## 前端结构（web/）

- `src/views/Login.vue` 登录；`src/views/Chat.vue` 问答主界面（SignalR 打字机、会话列表、知识库多选、引用卡片、Markdown 渲染 + DOMPurify 消毒）
- `src/views/Admin.vue` 管理后台：用户 / 角色 / 知识库（文档上传与状态轮询、字段 schema 表格编辑、用户与角色授权）
- `src/api/index.js` axios 封装（JWT 注入、401 自动登出）；`src/stores/auth.js` Pinia 会话


## 关键配置（生产环境全部走环境变量，禁止写入文件）

| 配置 | 环境变量 | 说明 |
|------|----------|------|
| 数据库 | `ConnectionStrings__Default` | PostgreSQL 连接串 |
| JWT 密钥 | `Jwt__SecretKey` | ≥32 字符 |
| 管理员初始密码 | `SmartKB__AdminInitialPassword` | 首次部署后必须修改 |
| LLM 模式 | `Llm__Mode` | `Agnes`（默认，Agnes AI）/ `Cloud`（智谱 API）/ `Local`（Ollama） |
| LLM Key | `Llm__Agnes__ApiKey` / `Llm__Cloud__ApiKey` | Agnes 与智谱 API Key，仅环境变量注入；Agnes 模式下向量化回退智谱 |

## API 一览（M1）

- `POST /api/auth/login` 登录 · `GET /api/auth/me` 当前用户 · `POST /api/auth/change-password` 改密
- `GET/POST/PUT /api/users`（admin）用户管理 · `PUT /api/users/{id}/password` 重置密码
- `GET/POST/DELETE /api/roles`（admin）角色管理
- `GET/POST/PUT/DELETE /api/kbs` 知识库 CRUD（非管理员仅见被授权库）
- `GET/POST/DELETE /api/kbs/permissions`（admin/kb_manager）授权管理
- `POST /api/kbs/{kbId}/documents`（multipart）上传 → 异步解析入库 · `GET` 列表含状态 · `POST {id}/retry` 重试 · `DELETE {id}` 删除
- `GET/POST/DELETE /api/chats` 问答会话管理 · `GET /api/chats/{id}/messages` 历史消息
- `GET /health` 健康检查

### 智能问答（SignalR `/hubs/chat`）

1. 连接：`/hubs/chat?access_token=<JWT>`（WebSocket 不支持自定义 header，token 走 query string）
2. 客户端调用：`Ask(sessionId, kbIds, question)` —— `sessionId` 传 null 自动建会话；`kbIds` 传空数组则检索全部有权限的库
3. 服务端事件（打字机）：
   - `token`（string）：增量文本，逐段推送
   - `citations`：引用出处数组（chunkId / documentId / fileName / seq / excerpt / score）
   - `done`：`{ sessionId, answer }` 完整回答，落库完成
   - `error`：错误消息

架构要点：MAF `ChatClientAgent` 编排 + 两个函数工具（`search_knowledge` 向量检索 / `query_ledger` 台账结构化查询），工具闭包捕获权限范围（`GetAccessibleKbIdsAsync`，管理员为 null 不过滤），LLM 无法越权；台账查询字段白名单校验 + 参数化 SQL，杜绝注入。
