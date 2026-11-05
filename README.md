# SmartKB 企业知识库智能问答系统

![SmartKB](docs/banner.svg)

.NET 10 + MAF + PostgreSQL(pgvector) 的可私有化交付知识库问答产品。

当前进度：**全部里程碑与增强项（P1 / P2）已完成，无剩余待办**，145 例单测全绿。功能总览见下文「核心功能」；实现状态与边界详见 `docs/方案设计.md` §0；私有化部署见 `docs/部署文档.md`。

## 项目结构

```
SmartKB.slnx
├─ src/SmartKB.Domain          实体与枚举（含 SysAuditLog、AgentTool、AgentToolGrant）
├─ src/SmartKB.Infrastructure  EF Core + Npgsql + pgvector、迁移、种子
├─ src/SmartKB.Application     业务服务（认证/用户/角色/知识库/授权/文档/会话/AI 观测/配置中心/审计/工具管理）
│                              + IKbAccessGuard 对象级授权守卫
├─ src/SmartKB.AI              AI 层（解析、切分、Embedding、RAG、MAF Agent、入库流水线）
│                              + 元数据过滤 / RRF 混合融合 / Rerank / RagSettings
│                              + Tools/ 配置化工具（定义校验、HTTP 执行器、动态 AIFunction、注册表与调用白名单）
├─ src/SmartKB.Api             WebApi 宿主（JWT、控制器、SignalR Hub、CurrentUser、中间件）
│                              + Health/ 分项探活、Filters/ 全局审计过滤器、Gateway/ OpenAI 兼容网关、Ldap/ 可选目录登录
├─ tests/SmartKB.Tests         xUnit 145 例（权限/台账/切分/文档归属/检索增强/配置中心/审计/探活/工具安全边界/工具授权与范围/网关协议与流程/文档授权/LDAP/OCR/队列模式）
├─ web/                        Vue3 + Element Plus 前端
├─ docker-compose.yml / .env.example / Dockerfile / nginx.conf
└─ docs/                       方案设计、部署文档、对标分析
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
- `src/views/Admin.vue` 管理后台：用户 / 角色 / 知识库（文档上传与状态轮询、字段 schema 表格编辑含 `filterable`、用户与角色授权、文档行级「授权」弹窗）/ **工具注册表** / **AI 观测** / **RAG 配置** / **审计日志**（后四项仅管理员）
- `src/components/KbPanel.vue` 知识库面板：文档列表含状态与重试/删除，文档行「授权」弹窗（用户/角色多选，无记录 = 跟随库权限）
- `src/components/ToolsPanel.vue` 工具注册表：参数表格编辑、URL/请求体模板、鉴权变量名、**知识库范围多选**（留空 = 全部库可用）、启停、「试运行」与**调用授权白名单**（用户/角色多选，列表显示公开/受限）
- `src/components/SettingsPanel.vue` 运行时配置表单：按提示词/检索/重排/生成分组，显示值与来源（默认 / 已覆盖），保存后立即生效，支持一键恢复默认
- `src/components/AuditPanel.vue` 审计日志：分类/成败/时间/关键字筛选、分页、今日操作与失败数
- `src/api/index.js` axios 封装（JWT 注入、401 自动登出）；`src/stores/auth.js` Pinia 会话

## 关键配置（生产环境全部走环境变量，禁止写入文件）

| 配置 | 环境变量 | 说明 |
|------|----------|------|
| 数据库 | `ConnectionStrings__Default` | PostgreSQL 连接串 |
| JWT 密钥 | `Jwt__SecretKey` | ≥32 字符 |
| 管理员初始密码 | `SmartKB__AdminInitialPassword` | 首次部署后必须修改 |
| LLM 模式 | `Llm__Mode` | `Agnes`（默认，Agnes AI）/ `Cloud`（智谱 API）/ `Local`（Ollama） |
| LLM Key | `Llm__Agnes__ApiKey` / `Llm__Cloud__ApiKey` | Agnes 与智谱 API Key，仅环境变量注入；Agnes 模式下向量化回退智谱 |
| 外部 Rerank Key | `RAG__Rerank__Api__ApiKey`（compose 中为 `RERANK_API_KEY`） | 选填；未配置或调用失败自动降级启发式重排 |
| 文件存储 | `SmartKB__FileStoragePath` | 本地默认 `AppData/files`；Docker 由 compose 固定为 `/app/storage`（对应 `kbstorage` 卷） |
| CORS 白名单 | `SmartKB__CorsAllowedOrigins__0` 等 | 生产默认**不开放任何跨域来源**（Nginx 同源反代）；开发环境在 `appsettings.Development.json` 配 `http://localhost:5173` |
| 工具主机白名单 | `Tools__AllowedHosts`（compose 中为 `TOOLS_ALLOWED_HOSTS`） | 选填，逗号/分号分隔；填了之后后台只能登记这些主机，留空表示信任后台登记的地址 |
| 业务系统密钥 | 按后台登记的变量名，如 `ERP_API_TOKEN` | **库里只存变量名**；`.env` 中的变量还需在 compose 的 `api.environment` 显式列出才会注入容器 |
| OpenAI 兼容网关 | `Gateway__Enabled` / `Gateway__ApiKey` / `Gateway__ServiceUsername` / `Gateway__AllowSystemPrompt` | 选填，**默认关闭**；三者齐备才启用，Key 仅环境变量；详见「核心功能 → OpenAI 兼容网关」 |
| LDAP/AD 登录 | `LDAP_ENABLED` / `LDAP_SERVER` / `LDAP_BASE_DN` / `LDAP_BIND_DN` / `LDAP_BIND_PASSWORD` 等 | 选填，**默认关闭**；绑定密码仅环境变量；详见「核心功能 → 企业集成」 |
| OCR sidecar | `OCR_ENABLED` / `OCR_ENDPOINT` / `OCR_API_KEY_ENV` | 选填，**默认关闭**；客户自备 HTTP OCR 服务，扫描件 PDF 自动补文本 |
| 文档队列模式 | `SmartKB__QueueMode`（compose 中为 `QUEUE_MODE`） | `Memory`（默认，单实例）/ `Database`（多实例安全，SKIP LOCKED 认领）；api 多实例部署必须切 `Database` |

## 核心功能

### 智能问答与 RAG 检索

- MAF `ChatClientAgent` 编排 + SignalR 打字机输出，回答带引用出处（文档名 / 片段序号 / 得分）。
- **元数据过滤**：字段只取知识库 schema 中 `filterable` 的字段，字段名与操作符（`eq/contains/gt/gte/lt/lte`）白名单校验；过滤与权限都在 SQL 层强制。
- **混合检索**（可开关）：pgvector 向量路 + PostgreSQL 全文关键词路（表达式 GIN 索引），RRF 融合 `1/(60+rank)`，不做量纲归一化。
- **重排**：启发式（零依赖：精确词覆盖率 + 融合分 + 长度惩罚）或外部 OpenAI 兼容 `/rerank` API（密钥仅环境变量）；外部失败自动降级启发式，绝不影响问答主流程。
- **运行时配置**：系统提示词、topK(8)、候选池(24)、温度(0.2)、最大输出 token(2048) 全部在「RAG 配置」后台可调，保存即生效，无需重启或发版；配置存 `sys_settings`，连接串与各类 ApiKey 不进配置中心。
- **内置工具**：`search_knowledge`（知识检索，支持 `filters` 元数据过滤）与 `query_ledger`（台账结构化查询，字段白名单校验 + 参数化 SQL 防注入）。

### 文档接入与解析

- 上传后异步解析、切分、向量化入库，前端状态轮询 + 失败重试。
- **OCR sidecar**（选填，默认关闭）：PDF 解析文本少于 120 字符（疑似扫描件）时，multipart POST 到客户自备的 HTTP OCR 服务补文本；未启用或失败维持原行为。
- **队列模式**：单实例 `Memory`（默认，进程内 Channel）；多实例 `Database`（PostgreSQL `FOR UPDATE SKIP LOCKED` 逐条认领，扩容不重复消费，启动时自动恢复遗留 Parsing 状态）。api 多实例部署必须切 `Database`。

### 权限与安全

- **三层授权**：角色粗筛（admin / kb_manager / 普通用户）→ 知识库对象级授权（服务层 `IKbAccessGuard` 守卫，越权 HTTP 403，控制器只做粗筛）→ **文档级授权**（文档无任何授权记录 = 跟随库权限，升级零迁移成本；一旦配置，仅白名单用户/角色与库管理员可检索到，Manage 权限不受限）。
- **全链路同源过滤**：检索（向量路 + 关键词路）、台账查询、文档列表全部按权限与文档级授权过滤，在服务层与 SQL 层强制，不依赖前端隐藏；文档 ID 操作强制同时匹配 `Id` 与 `KbId`，杜绝跨库删改；授权入口防「给自己授权到无权库」绕过。
- **密钥管理**：JWT / LLM / 网关 / LDAP / 工具鉴权密钥一律只走环境变量，库里只存变量名、不回显。
- **CORS**：生产默认不开放任何跨域来源（Nginx 同源反代），白名单走配置。

### 可扩展工具（不改代码接入内部接口）

- 后台「工具注册表」（仅管理员）登记内部 HTTP 接口：参数表格编辑生成 function JSON Schema、URL/请求体模板、鉴权头 + 密钥环境变量名、超时 1–30 秒与响应截断、启停、「试运行」。模型按需调用，与内置检索/台账工具并列。
- **安全边界**：scheme/host/port 来自配置，参数只替换 authority 之后的 path/query 并 URL 编码，拼完再次断言主机未变——LLM 无法把请求打到别的机器；不跟随 3xx 重定向；可用 `Tools__AllowedHosts` 锁死目标主机；任何失败（配置错、缺参、密钥未配、超时、上游 5xx）降级为可读文本让模型继续回答；坏配置只跳过并记 Warning。
- **调用白名单**：无记录 = 公开（所有可登录用户可用）；配置后仅白名单用户/角色与管理员可见，过滤发生在注入模型之前（不进 `ChatOptions.Tools`，连模型都看不到）。
- **知识库范围**：限定工具只服务于指定知识库，与本次问答范围无交集时不注入该工具；留空 = 全部可用。
- **建议只登记只读接口**：试运行和模型调用都会真实产生副作用；写操作全部进审计日志。

### OpenAI 兼容网关（选填，默认关闭）

客户已有 OpenAI SDK / ChatGPT 客户端可直接接入，无需改造 SignalR：

```bash
# .env 中启用：GATEWAY_ENABLED / GATEWAY_API_KEY / GATEWAY_SERVICE_USERNAME（三者齐备才启用，服务账号先在后台创建并授权知识库）
curl http://localhost/v1/models -H "Authorization: Bearer $GATEWAY_API_KEY"
curl http://localhost/v1/chat/completions \
  -H "Authorization: Bearer $GATEWAY_API_KEY" -H 'Content-Type: application/json' \
  -d '{"model":"smartkb","messages":[{"role":"user","content":"保修期多久？"}]}'
# 流式加 "stream":true，标准 SSE + data: [DONE]；引用走扩展字段 smartkb_citations
```

- Key 绑定真实服务账号（权限完全按该账号计算，**切勿绑 admin**），SHA256 + 定长比较、不回显不进日志；也接受现有用户 JWT。
- 权限不因协议放宽：`kb_ids` / `model = smartkb:1,2` 只是请求范围，仍与账号可访问库求交；`session_id` 校验归属；system 消息默认忽略（`Gateway__AllowSystemPrompt=true` 才允许覆盖）；`temperature` / `max_tokens` 允许请求覆盖并做范围校验。
- 每次调用落会话、消息与 AI 观测，与 SignalR 共用；不传 `session_id` 新建 `[API]` 会话。
- 边界：网关不做限流（建议 Nginx 对 `/v1/` 限流）；写请求不走全局审计过滤器（只覆盖 `/api`），靠会话与观测留痕。

### 企业集成

- **LDAP/AD 登录**（选填，默认关闭）：本地密码优先；本地校验失败且启用时走 search-then-bind 域控验证，通过即**自动开通**本地账号并挂 `LDAP_DEFAULT_ROLE`（须为已存在角色，留空由管理员手动授权）。自动开通账号的密码哈希是随机值，本地密码路径不可登录，认证完全交给域控；登录名做 RFC 4515 转义防过滤器注入；绑定密码只走环境变量。
- **OCR sidecar**（选填，默认关闭）：客户自备 HTTP OCR 服务，响应取 JSON `text` 字段或纯文本，见「文档接入与解析」。

### 运维与观测

- **AI 观测**：每次问答与网关调用自动落 `ai_invocation_logs`（含失败/断连）——token 用量、首字延迟（TTFT）、总耗时、工具调用次数、命中数；后台按汇总 / 日趋势 / 明细查看（仅管理员）。
- **分项健康检查**：`GET /health` 匿名可访问，database / pgvector（扩展、HNSW、全文索引）/ storage / llm 四项。`healthy` 200；`degraded` 200（LLM 抖动与索引缺失只降级不判死，避免编排器误重启容器）；`unhealthy` 503（数据库连不上、存储不可写）。LLM 探活结果缓存 10 秒，防监控高频轮询触发厂商限流；密钥绝不回显。
- **操作审计**：全局过滤器自动记录 `/api` 下所有写操作（登录、用户/角色/知识库/文档/授权、配置变更、工具与文档授权），落 `sys_audit_logs`——操作人、语义化动作、路径、状态码、耗时、IP/UA、失败原因；**只记元数据不记请求/响应体**（密码、ApiKey、文档正文不入库），GET 不记录；后台面板分类/成败/时间/关键字筛选 + 分页 + 今日统计。表会持续增长，按合规要求定期归档。

## 测试

```bash
dotnet test SmartKB.slnx
```

145 例覆盖：权限守卫与文档/工具授权闭环、切分边界、文档归属、检索增强（元数据白名单/六种操作符/RRF 融合/关键词提取/重排与降级）、配置中心（覆盖回退/范围钳制/缓存失效/`sys_settings` 读写）、审计查询与写入容错、健康分级/脱敏/探活缓存、工具参数 DSL 与静态校验、URL host 固定与编码、密钥不回显、动态 AIFunction schema、注册表缓存、网关协议（OpenAI 请求解析/model 路由/Key 定长比较/SSE 分块/`[DONE]`/usage 扩展/错误格式/system 覆盖开关）与权限闭包、LDAP 自动开通与过滤器转义、OCR 门槛/响应解析/入库降级、队列模式解析与双实现语义。

## API 一览

- `POST /api/auth/login` 登录 · `GET /api/auth/me` 当前用户 · `POST /api/auth/change-password` 改密
- `GET/POST/PUT /api/users`（admin）用户管理 · `PUT /api/users/{id}/password` 重置密码
- `GET/POST/DELETE /api/roles`（admin）角色管理
- `GET/POST/PUT/DELETE /api/kbs` 知识库 CRUD（非管理员仅见被授权库）
- `GET/POST/DELETE /api/kbs/permissions`（admin/kb_manager）授权管理
- `POST /api/kbs/{kbId}/documents`（multipart）上传 → 异步解析入库 · `GET` 列表含状态 · `POST {id}/retry` 重试 · `DELETE {id}` 删除 · `GET/PUT {id}/grants` 文档级授权（仅 Manage）
- `GET/POST/DELETE /api/chats` 问答会话管理 · `GET /api/chats/{id}/messages` 历史消息
- `GET /api/ai-usage/summary|daily|recent`（仅管理员）AI 调用观测：每次问答的 token 用量、首字延迟（TTFT）、总耗时、工具调用次数、命中数；每次问答（含失败/断连）自动落库 `ai_invocation_logs`
- `GET/PUT /api/settings` 与 `POST /api/settings/reset`（仅管理员）RAG 运行时配置
- `GET /api/audit-logs`（仅管理员）操作审计日志分页查询
- `GET/POST/PUT/DELETE /api/agent-tools`、`PATCH /api/agent-tools/{id}/enabled`、`POST /api/agent-tools/test`、`GET/PUT /api/agent-tools/{id}/grants`（仅管理员）配置化工具管理、试运行与调用授权
- `GET /v1/models`、`POST /v1/chat/completions`（网关 Key 或 JWT）OpenAI 兼容网关，默认关闭
- `GET /health`（匿名）分项健康检查；`unhealthy` 返回 503，`degraded` 返回 200

### 智能问答（SignalR `/hubs/chat`）

1. 连接：`/hubs/chat?access_token=<JWT>`（WebSocket 不支持自定义 header，token 走 query string）
2. 客户端调用：`Ask(sessionId, kbIds, question)` —— `sessionId` 传 null 自动建会话；`kbIds` 传空数组则检索全部有权限的库
3. 服务端事件（打字机）：
   - `token`（string）：增量文本，逐段推送
   - `citations`：引用出处数组（chunkId / documentId / fileName / seq / excerpt / score）
   - `done`：`{ sessionId, answer }` 完整回答，落库完成
   - `error`：错误消息

架构要点：MAF `ChatClientAgent` 编排 + 内置两个函数工具（`search_knowledge` 知识检索，支持 `filters` 元数据过滤 / `query_ledger` 台账结构化查询）+ 配置化 HTTP 工具（动态 `AIFunction` 子类，schema 来自 `sys_agent_tools`）。工具闭包捕获权限范围（`GetAccessibleKbIdsAsync`，管理员为 null 不过滤），并显式传递调用者身份（`ToolCaller`）——工具按可访问库、调用白名单、知识库范围与文档级授权逐层过滤后才注入模型，LLM 无法越权。台账查询字段白名单校验 + 参数化 SQL，杜绝注入。系统提示词与生成参数由 `IRagSettingsProvider` 在每次问答时注入，网关可通过 `ChatRequestOptions` 有限覆盖；AI 层通过依赖倒置不反向引用业务层。
