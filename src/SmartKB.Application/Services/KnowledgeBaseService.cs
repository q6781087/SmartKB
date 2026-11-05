using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartKB.Application.Dtos;
using SmartKB.Domain.Entities;
using SmartKB.Domain.Enums;
using SmartKB.Infrastructure;

namespace SmartKB.Application.Services;

public interface IKnowledgeBaseService
{
    Task<List<KnowledgeBaseDto>> GetListAsync(long userId, bool isAdmin);
    Task<KnowledgeBaseDto> CreateAsync(SaveKnowledgeBaseRequest request);
    Task<KnowledgeBaseDto> UpdateAsync(long id, SaveKnowledgeBaseRequest request);
    Task DeleteAsync(long id);
}

/// <summary>
/// 知识库管理：字段 schema 用 JSON 数组存储，保存前做结构校验。
/// 变更与删除在服务层强制授权，控制器只保留角色粗筛。
/// </summary>
public class KnowledgeBaseService(
    SmartKbDbContext db,
    IKbAccessGuard guard,
    ICurrentUser currentUser) : IKnowledgeBaseService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { AllowTrailingCommas = true };

    public async Task<List<KnowledgeBaseDto>> GetListAsync(long userId, bool isAdmin)
    {
        var query = db.KnowledgeBases.AsNoTracking();
        if (!isAdmin)
        {
            // 非管理员只看到被授权的库（用户直授权或角色授权）
            var roleIds = await db.Users.Where(u => u.Id == userId)
                .SelectMany(u => u.Roles.Select(r => r.Id)).ToListAsync();
            query = query.Where(kb => db.Permissions.Any(p =>
                p.KbId == kb.Id &&
                (p.UserId == userId || (p.RoleId != null && roleIds.Contains(p.RoleId.Value)))));
        }

        return await query.Select(kb => new KnowledgeBaseDto(
            kb.Id, kb.Name, kb.Description, kb.FieldSchemaJson, kb.EnableLedger,
            kb.CreatedAt, kb.Documents.Count)).ToListAsync();
    }

    public async Task<KnowledgeBaseDto> CreateAsync(SaveKnowledgeBaseRequest request)
    {
        ValidateSchema(request.FieldSchemaJson);
        if (await db.KnowledgeBases.AnyAsync(kb => kb.Name == request.Name))
            throw new BusinessRuleException($"知识库 {request.Name} 已存在");

        var kb = new KnowledgeBase
        {
            Name = request.Name,
            Description = request.Description,
            FieldSchemaJson = request.FieldSchemaJson,
            EnableLedger = request.EnableLedger
        };
        db.KnowledgeBases.Add(kb);
        await db.SaveChangesAsync(); // 先拿 kb.Id

        // 非管理员建库后必须立刻拿到 Manage 授权：建库接口对 kb_manager 开放，
        // 若依赖管理员事后补授权，服务层守卫会让创建者连自己建的库都管不了。
        if (!currentUser.IsAdmin)
        {
            db.Permissions.Add(new KbPermission
            {
                KbId = kb.Id,
                UserId = currentUser.UserId,
                Level = PermissionLevel.Manage
            });
            await db.SaveChangesAsync();
        }

        return ToDto(kb, 0);
    }

    public async Task<KnowledgeBaseDto> UpdateAsync(long id, SaveKnowledgeBaseRequest request)
    {
        await guard.AssertManageAsync(id);
        ValidateSchema(request.FieldSchemaJson);

        var kb = await db.KnowledgeBases.Include(k => k.Documents).FirstOrDefaultAsync(k => k.Id == id)
            ?? throw new BusinessRuleException("知识库不存在");

        if (kb.Name != request.Name &&
            await db.KnowledgeBases.AnyAsync(k => k.Name == request.Name))
            throw new BusinessRuleException($"知识库 {request.Name} 已存在");

        kb.Name = request.Name;
        kb.Description = request.Description;
        kb.FieldSchemaJson = request.FieldSchemaJson;
        kb.EnableLedger = request.EnableLedger;
        await db.SaveChangesAsync();
        return ToDto(kb, kb.Documents.Count);
    }

    public Task DeleteAsync(long id)
    {
        // 删库是系统级操作，维持原有的"仅 admin"语义，服务层再兜一层
        guard.AssertAdmin();
        return DeleteCoreAsync(id);
    }

    private async Task DeleteCoreAsync(long id)
    {
        var kb = await db.KnowledgeBases
            .Include(k => k.Documents)
            .FirstOrDefaultAsync(k => k.Id == id)
            ?? throw new BusinessRuleException("知识库不存在");

        if (kb.Documents.Count > 0)
            throw new BusinessRuleException($"该库仍有 {kb.Documents.Count} 个文档，请先清空（避免向量孤儿数据）");

        db.KnowledgeBases.Remove(kb);
        await db.SaveChangesAsync();
    }

    /// <summary>校验字段 schema：合法 JSON 数组，元素含 name/label/type，name 不重复</summary>
    private static void ValidateSchema(string schemaJson)
    {
        List<JsonElement> fields;
        try
        {
            fields = JsonSerializer.Deserialize<List<JsonElement>>(schemaJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            throw new BusinessRuleException("字段定义不是合法的 JSON 数组");
        }

        var names = new HashSet<string>();
        foreach (var f in fields)
        {
            if (f.ValueKind != JsonValueKind.Object ||
                !f.TryGetProperty("name", out var n) || string.IsNullOrWhiteSpace(n.GetString()) ||
                !f.TryGetProperty("label", out var l) || string.IsNullOrWhiteSpace(l.GetString()))
                throw new BusinessRuleException("字段定义元素必须包含非空 name 与 label");

            var type = f.TryGetProperty("type", out var t) ? t.GetString() : "string";
            if (type is not ("string" or "number" or "date"))
                throw new BusinessRuleException("字段 type 仅支持 string / number / date");

            if (!names.Add(n.GetString()!))
                throw new BusinessRuleException($"字段 name 重复：{n.GetString()}");
        }
    }

    private static KnowledgeBaseDto ToDto(KnowledgeBase kb, int docCount) =>
        new(kb.Id, kb.Name, kb.Description, kb.FieldSchemaJson, kb.EnableLedger, kb.CreatedAt, docCount);
}
