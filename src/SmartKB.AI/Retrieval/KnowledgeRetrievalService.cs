using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using SmartKB.AI.Configuration;
using SmartKB.AI.Embedding;
using SmartKB.Infrastructure;

namespace SmartKB.AI.Retrieval;

/// <summary>检索结果片段（引用溯源最小单元）</summary>
public record RetrievedChunk(
    long ChunkId,
    long DocumentId,
    string FileName,
    int Seq,
    string Content,
    string? Title,
    float Score);

/// <summary>
/// 知识库检索：向量余弦 +（可选）PostgreSQL 全文/精确词双路，RRF 融合 + 可选重排。
///
/// 安全边界（都在 SQL 层强制，不是前端隐藏）：
/// 1. accessibleKbIds 由调用方从 IPermissionService 取得，null 表示管理员不过滤；
/// 2. 元数据过滤先算出允许的 documentId 集合，两路召回都带 DocumentId IN (...) 条件；
/// 3. 文档级授权（v1.6）：有授权记录的文档仅白名单用户/角色可见，无记录的文档跟随库权限。
/// </summary>
public interface IKnowledgeRetrievalService
{
    Task<List<RetrievedChunk>> SearchAsync(
        string question, List<long>? accessibleKbIds, int topK = 8, CancellationToken ct = default);

    Task<List<RetrievedChunk>> SearchAsync(
        string question, List<long>? accessibleKbIds, int topK,
        IReadOnlyList<MetadataFilter>? filters, CancellationToken ct = default);

    /// <summary>带调用者身份的检索：文档级授权在此强制（caller=null 视为管理员）</summary>
    Task<List<RetrievedChunk>> SearchAsync(
        string question, List<long>? accessibleKbIds, int topK,
        IReadOnlyList<MetadataFilter>? filters, Tools.ToolCaller? caller, CancellationToken ct = default);

    /// <summary>当前检索范围内，客户配置为 filterable 的字段（供工具描述与白名单校验）</summary>
    Task<List<FilterableField>> GetFilterableFieldsAsync(
        List<long>? accessibleKbIds, CancellationToken ct = default);
}

public class KnowledgeRetrievalService(
    SmartKbDbContext db,
    IEmbeddingProvider embeddingProvider,
    IRagSettingsProvider settingsProvider,
    IRerankService rerank) : IKnowledgeRetrievalService
{
    /// <summary>文档级授权的调用者上下文（null = 管理员/系统调用，不做文档过滤）</summary>
    private sealed record DocAcl(long UserId, HashSet<long> RoleIds);

    public Task<List<RetrievedChunk>> SearchAsync(
        string question, List<long>? accessibleKbIds, int topK = 8, CancellationToken ct = default)
        => SearchAsync(question, accessibleKbIds, topK, null, null, ct);

    public Task<List<RetrievedChunk>> SearchAsync(
        string question, List<long>? accessibleKbIds, int topK,
        IReadOnlyList<MetadataFilter>? filters, CancellationToken ct = default)
        => SearchAsync(question, accessibleKbIds, topK, filters, null, ct);

    public async Task<List<RetrievedChunk>> SearchAsync(
        string question, List<long>? accessibleKbIds, int topK,
        IReadOnlyList<MetadataFilter>? filters, Tools.ToolCaller? caller, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return [];
        if (accessibleKbIds is { Count: 0 }) return [];

        var settings = await settingsProvider.GetAsync(ct);
        var finalTopK = topK > 0 ? Math.Min(topK, 30) : Math.Min(settings.TopK, 30);
        var candidateK = Math.Clamp(Math.Max(settings.CandidateTopK, finalTopK * 2), finalTopK, 100);

        // ---------- 1. 元数据过滤：校验白名单 → 解析出允许检索的文档 ID 集合 ----------
        List<long>? allowedDocIds = null;
        if (settings.MetadataFilterEnabled && filters is { Count: > 0 })
        {
            var fields = await GetFilterableFieldsAsync(accessibleKbIds, ct);
            MetadataFilterEngine.Validate(filters, fields); // 非法字段/操作符直接抛，由工具转成可读提示
            allowedDocIds = await ResolveAllowedDocumentIdsAsync(accessibleKbIds, filters, ct);
            if (allowedDocIds.Count == 0) return [];
        }

        // ---------- 1.5 文档级授权：有授权记录的文档仅白名单用户/角色可见 ----------
        var acl = await ResolveDocAclAsync(accessibleKbIds, caller, ct);

        // ---------- 2. 向量路（EF LINQ，pgvector HNSW 索引） ----------
        var vectors = await embeddingProvider.EmbedBatchAsync([question.Trim()], ct);
        var queryVector = new Vector(vectors[0]);

        var vq = db.Chunks.AsNoTracking().AsQueryable();
        if (accessibleKbIds is not null)
            vq = vq.Where(c => accessibleKbIds.Contains(c.KbId));
        if (allowedDocIds is not null)
            vq = vq.Where(c => allowedDocIds.Contains(c.DocumentId));
        if (acl is not null)
            vq = vq.Where(c =>
                !db.DocumentGrants.Any(g => g.DocumentId == c.DocumentId) ||
                db.DocumentGrants.Any(g => g.DocumentId == c.DocumentId &&
                    (g.UserId == acl.UserId || (g.RoleId != null && acl.RoleIds.Contains(g.RoleId.Value)))));

        var vectorRows = await vq
            .OrderBy(c => c.Embedding!.CosineDistance(queryVector))
            .Take(candidateK)
            .Select(c => new { c.Id, Distance = c.Embedding!.CosineDistance(queryVector) })
            .ToListAsync(ct);

        var vectorIds = vectorRows.Select(r => r.Id).ToList();
        if (vectorIds.Count == 0 && !settings.HybridEnabled) return [];

        // ---------- 3. 关键词路（原生 SQL：tsvector + 精确词 ILIKE 兜底） ----------
        List<long> keywordIds = [];
        if (settings.HybridEnabled && db.Database.IsRelational())
            keywordIds = await KeywordSearchAsync(question, accessibleKbIds, allowedDocIds, acl, candidateK, ct);

        if (vectorIds.Count == 0 && keywordIds.Count == 0) return [];

        // ---------- 4. RRF 融合 ----------
        var fused = keywordIds.Count > 0
            ? HybridFusion.RrfFuse(vectorIds, keywordIds, settings.RrfK)
            : HybridFusion.Single(vectorIds, settings.RrfK);

        var poolSize = settings.RerankEnabled
            ? Math.Max(settings.RerankTopN, finalTopK)
            : finalTopK;
        var pool = fused.Take(Math.Max(poolSize, finalTopK)).ToList();
        var poolIds = pool.Select(p => p.ChunkId).ToList();

        // ---------- 5. 取回完整内容（只对候选 ID 查询，不重复算向量） ----------
        var rows = await db.Chunks.AsNoTracking()
            .Where(c => poolIds.Contains(c.Id))
            .Join(db.Documents, c => c.DocumentId, d => d.Id, (c, d) => new
            {
                c.Id, c.DocumentId, c.Seq, c.Content, c.MetadataJson, d.FileName
            })
            .ToListAsync(ct);

        var byId = rows.ToDictionary(r => r.Id);
        var scoreById = pool.ToDictionary(p => p.ChunkId, p => (float)Math.Min(1.0, p.Score * 60));

        // ---------- 6. 可选重排（失败降级由 RerankService 内部保证） ----------
        var orderedIds = poolIds.Where(byId.ContainsKey).ToList();
        if (settings.RerankEnabled && orderedIds.Count > 1)
        {
            var candidates = orderedIds.Select(id =>
                new RerankCandidate(id, byId[id].Content, scoreById.GetValueOrDefault(id))).ToList();
            var ranked = await rerank.RerankAsync(
                settings.RerankProvider, question, candidates, finalTopK, ct);
            var rankedSet = ranked.Where(byId.ContainsKey).ToList();
            // 重排结果不足时，用融合顺序补齐，保证不因外部服务少返回而丢内容
            orderedIds = rankedSet.Concat(orderedIds.Where(id => !rankedSet.Contains(id))).ToList();
        }

        return [.. orderedIds.Take(finalTopK).Select(id =>
        {
            var r = byId[id];
            string? title = null;
            try
            {
                using var meta = JsonDocument.Parse(r.MetadataJson);
                if (meta.RootElement.TryGetProperty("title", out var t)) title = t.GetString();
            }
            catch (JsonException) { /* 元数据非 JSON 时忽略 */ }

            return new RetrievedChunk(
                r.Id, r.DocumentId, r.FileName, r.Seq, r.Content, title,
                scoreById.GetValueOrDefault(id));
        })];
    }

    public async Task<List<FilterableField>> GetFilterableFieldsAsync(
        List<long>? accessibleKbIds, CancellationToken ct = default)
    {
        var q = db.KnowledgeBases.AsNoTracking().AsQueryable();
        if (accessibleKbIds is not null)
            q = q.Where(k => accessibleKbIds.Contains(k.Id));
        var schemas = await q.Select(k => k.FieldSchemaJson).ToListAsync(ct);
        return MetadataFilterEngine.ParseFilterableFields(schemas);
    }

    /// <summary>
    /// 元数据过滤下推：先用第一个过滤值做 SQL 字面粗筛（strpos/LIKE），
    /// 再在内存按 JSON 精确比对全部条件，得到允许检索的文档 ID 集合。
    /// 权限范围（KbId）在这一步同样强制，避免跨库读元数据。
    /// </summary>
    private async Task<List<long>> ResolveAllowedDocumentIdsAsync(
        List<long>? accessibleKbIds, IReadOnlyList<MetadataFilter> filters, CancellationToken ct)
    {
        var q = db.Documents.AsNoTracking().AsQueryable();
        if (accessibleKbIds is not null)
            q = q.Where(d => accessibleKbIds.Contains(d.KbId));

        var probe = filters.Select(f => f.Value.Trim())
            .FirstOrDefault(v => v.Length >= 2 && v.Length <= 128);
        if (probe is not null)
            q = q.Where(d => d.MetadataJson.Contains(probe));

        var docs = await q.Select(d => new { d.Id, d.MetadataJson }).ToListAsync(ct);
        return [.. docs
            .Where(d => MetadataFilterEngine.Matches(d.MetadataJson, filters))
            .Select(d => d.Id)];
    }

    /// <summary>
    /// 文档级授权：存在授权记录时按调用者解析；管理员 / 系统调用 / 全库无授权记录时不启用。
    /// </summary>
    private async Task<DocAcl?> ResolveDocAclAsync(
        List<long>? kbScope, Tools.ToolCaller? caller, CancellationToken ct)
    {
        if (caller is null || caller.IsAdmin || kbScope is { Count: 0 }) return null;
        if (!await db.DocumentGrants.AsNoTracking().AnyAsync(ct)) return null;

        var roleIds = await db.Users.AsNoTracking()
            .Where(u => u.Id == caller.UserId)
            .SelectMany(u => u.Roles.Select(r => r.Id))
            .ToListAsync(ct);
        return new DocAcl(caller.UserId, roleIds.ToHashSet());
    }

    /// <summary>
    /// 关键词路：PostgreSQL 内核自带 tsvector（无需额外扩展），配 ILIKE 精确词兜底中文。
    /// 权限 + 文档白名单 + 文档级授权在同一条 SQL 内，与向量路共用安全边界。
    /// </summary>
    private async Task<List<long>> KeywordSearchAsync(
        string question, List<long>? accessibleKbIds, List<long>? allowedDocIds,
        DocAcl? acl, int take, CancellationToken ct)
    {
        if (accessibleKbIds is { Count: 0 } || allowedDocIds is { Count: 0 }) return [];

        var keywords = HybridFusion.ExtractKeywords(question);
        var clauses = new List<string>();
        // 表达式必须与迁移里的 GIN 索引完全一致，否则索引不命中、退化成全表 tsvector 计算：
        //   to_tsvector('simple'::regconfig, "Content")  （两参数 IMMUTABLE 版本）
        var sql = """
            SELECT "Id" FROM kb_chunks
            WHERE {kbFilter} AND {docFilter} AND {aclFilter} AND (
                to_tsvector('simple'::regconfig, "Content") @@ websearch_to_tsquery('simple'::regconfig, @q)
                {likeClauses}
            )
            ORDER BY ts_rank(to_tsvector('simple'::regconfig, "Content"),
                             websearch_to_tsquery('simple'::regconfig, @q)) DESC, "Id"
            LIMIT @take
            """;

        var conn = db.Database.GetDbConnection();
        var closeAfter = conn.State != System.Data.ConnectionState.Open;
        if (closeAfter) await conn.OpenAsync(ct);

        try
        {
            await using var cmd = conn.CreateCommand();

            void AddParam(string name, object value)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = name.TrimStart('@');
                p.Value = value;
                cmd.Parameters.Add(p);
            }

            AddParam("q", question.Length > 512 ? question[..512] : question);
            AddParam("take", take);

            string kbFilter = "TRUE";
            if (accessibleKbIds is not null)
            {
                var names = new List<string>();
                for (var i = 0; i < accessibleKbIds.Count; i++)
                {
                    var n = $"@kb{i}";
                    AddParam(n, accessibleKbIds[i]);
                    names.Add(n);
                }
                kbFilter = $"\"KbId\" IN ({string.Join(",", names)})";
            }

            string docFilter = "TRUE";
            if (allowedDocIds is not null)
            {
                var names = new List<string>();
                for (var i = 0; i < allowedDocIds.Count; i++)
                {
                    var n = $"@doc{i}";
                    AddParam(n, allowedDocIds[i]);
                    names.Add(n);
                }
                docFilter = $"\"DocumentId\" IN ({string.Join(",", names)})";
            }

            // 文档级授权：有授权记录的文档仅白名单可见（子查询下推，不物化 ID 列表）
            string aclFilter = "TRUE";
            if (acl is not null)
            {
                AddParam("aclUid", acl.UserId);
                var aclConds = new List<string> { "\"UserId\" = @aclUid" };
                if (acl.RoleIds.Count > 0)
                {
                    var roleNames = new List<string>();
                    var i = 0;
                    foreach (var roleId in acl.RoleIds)
                    {
                        var n = $"@aclr{i++}";
                        AddParam(n, roleId);
                        roleNames.Add(n);
                    }
                    aclConds.Add($"\"RoleId\" IN ({string.Join(",", roleNames)})");
                }
                var granted = $"SELECT \"DocumentId\" FROM kb_document_grants WHERE {string.Join(" OR ", aclConds)}";
                aclFilter = "(\"DocumentId\" NOT IN (SELECT \"DocumentId\" FROM kb_document_grants) " +
                            $"OR \"DocumentId\" IN ({granted}))";
            }

            for (var i = 0; i < keywords.Count; i++)
            {
                AddParam($"@kw{i}", $"%{keywords[i]}%");
                clauses.Add($"OR \"Content\" ILIKE @kw{i}");
            }

            cmd.CommandText = sql
                .Replace("{kbFilter}", kbFilter)
                .Replace("{docFilter}", docFilter)
                .Replace("{aclFilter}", aclFilter)
                .Replace("{likeClauses}", string.Join(" ", clauses));

            var ids = new List<long>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                ids.Add(reader.GetInt64(0));
            return ids;
        }
        finally
        {
            if (closeAfter) await conn.CloseAsync();
        }
    }
}
