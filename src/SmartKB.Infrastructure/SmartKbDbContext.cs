using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using SmartKB.Domain.Entities;

namespace SmartKB.Infrastructure;

/// <summary>EF Core 数据上下文（PostgreSQL + pgvector）</summary>
public class SmartKbDbContext(DbContextOptions<SmartKbDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<KnowledgeBase> KnowledgeBases => Set<KnowledgeBase>();
    public DbSet<KbDocument> Documents => Set<KbDocument>();
    public DbSet<KbChunk> Chunks => Set<KbChunk>();
    public DbSet<KbLedgerRow> LedgerRows => Set<KbLedgerRow>();
    public DbSet<KbPermission> Permissions => Set<KbPermission>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<SysSetting> Settings => Set<SysSetting>();
    public DbSet<AiInvocationLog> AiInvocationLogs => Set<AiInvocationLog>();

    public DbSet<AgentTool> AgentTools => Set<AgentTool>();
    public DbSet<AgentToolGrant> AgentToolGrants => Set<AgentToolGrant>();
    public DbSet<KbDocumentGrant> DocumentGrants => Set<KbDocumentGrant>();
    public DbSet<SysAuditLog> AuditLogs => Set<SysAuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("sys_users");
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
            e.Property(x => x.Department).HasMaxLength(128);
        });

        modelBuilder.Entity<Role>(e =>
        {
            e.ToTable("sys_roles");
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
        });

        // 用户-角色 多对多
        modelBuilder.Entity<User>()
            .HasMany(u => u.Roles)
            .WithMany(r => r.Users)
            .UsingEntity("sys_user_roles",
                l => l.HasOne(typeof(Role)).WithMany().HasForeignKey("RoleId").HasPrincipalKey(nameof(Role.Id)),
                r => r.HasOne(typeof(User)).WithMany().HasForeignKey("UserId").HasPrincipalKey(nameof(User.Id)));

        modelBuilder.Entity<KnowledgeBase>(e =>
        {
            e.ToTable("kb_knowledge_bases");
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<KbDocument>(e =>
        {
            e.ToTable("kb_documents");
            e.HasIndex(x => new { x.KbId, x.Status });
            e.Property(x => x.FileName).HasMaxLength(256).IsRequired();
            e.Property(x => x.FilePath).HasMaxLength(512).IsRequired();
        });

        modelBuilder.Entity<KbChunk>(e =>
        {
            e.ToTable("kb_chunks");
            e.HasIndex(x => new { x.KbId, x.DocumentId });

            // pgvector 列只有关系型 provider（生产 Npgsql）能映射；
            // 非关系型 provider（如单测用的 InMemory）忽略该列，业务表查询不受影响。
            if (Database.IsRelational())
            {
                e.Property(x => x.Embedding).HasColumnType("vector(1024)");
                // HNSW 余弦相似度索引（RAG 检索主索引）
                e.HasIndex(x => x.Embedding)
                    .HasMethod("hnsw")
                    .HasOperators("vector_cosine_ops");
            }
            else
            {
                e.Ignore(x => x.Embedding);
            }
        });

        modelBuilder.Entity<KbLedgerRow>(e =>
        {
            e.ToTable("kb_ledger_rows");
            e.HasIndex(x => new { x.KbId, x.SourceDocumentId });
        });

        modelBuilder.Entity<KbPermission>(e =>
        {
            e.ToTable("kb_permissions");
            e.HasIndex(x => new { x.KbId, x.UserId, x.RoleId }).IsUnique();
        });

        modelBuilder.Entity<ChatSession>(e =>
        {
            e.ToTable("chat_sessions");
            e.HasIndex(x => new { x.UserId, x.LastActiveAt });
        });

        modelBuilder.Entity<ChatMessage>(e =>
        {
            e.ToTable("chat_messages");
            e.HasIndex(x => x.SessionId);
        });

        modelBuilder.Entity<SysSetting>(e =>
        {
            e.ToTable("sys_settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
        });

        modelBuilder.Entity<AiInvocationLog>(e =>
        {
            e.ToTable("ai_invocation_logs");
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.SessionId, x.Id });
            e.Property(x => x.Model).HasMaxLength(128).IsRequired();
            e.Property(x => x.Question).HasMaxLength(200).IsRequired();
            e.Property(x => x.ErrorMessage).HasMaxLength(500);
        });

        modelBuilder.Entity<AgentTool>(e =>
        {
            e.ToTable("sys_agent_tools");
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.Enabled);
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000).IsRequired();
            e.Property(x => x.HttpMethod).HasMaxLength(8).IsRequired();
            e.Property(x => x.UrlTemplate).HasMaxLength(1000).IsRequired();
            e.Property(x => x.AuthHeaderName).HasMaxLength(64);
            e.Property(x => x.AuthEnvVar).HasMaxLength(64);
            e.Property(x => x.AuthValuePrefix).HasMaxLength(64);
        });

        // 工具调用白名单：工具/工具删除时级联清理；UserId 与 RoleId 二选一
        modelBuilder.Entity<AgentToolGrant>(e =>
        {
            e.ToTable("sys_agent_tool_grants", t =>
                t.HasCheckConstraint("CK_agent_tool_grants_target",
                    "\"UserId\" IS NOT NULL OR \"RoleId\" IS NOT NULL"));
            e.HasIndex(x => new { x.ToolId, x.UserId }).IsUnique();
            e.HasIndex(x => new { x.ToolId, x.RoleId }).IsUnique();
            e.HasOne<AgentTool>().WithMany().HasForeignKey(x => x.ToolId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 文档级授权：文档删除时级联清理；UserId 与 RoleId 二选一（与工具白名单同语义）
        modelBuilder.Entity<KbDocumentGrant>(e =>
        {
            e.ToTable("kb_document_grants", t =>
                t.HasCheckConstraint("CK_document_grants_target",
                    "\"UserId\" IS NOT NULL OR \"RoleId\" IS NOT NULL"));
            e.HasIndex(x => new { x.DocumentId, x.UserId }).IsUnique();
            e.HasIndex(x => new { x.DocumentId, x.RoleId }).IsUnique();
            e.HasOne<KbDocument>().WithMany().HasForeignKey(x => x.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SysAuditLog>(e =>
        {
            e.ToTable("sys_audit_logs");            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.Category, x.CreatedAt });
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Category).HasMaxLength(32).IsRequired();
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
            e.Property(x => x.Path).HasMaxLength(256).IsRequired();
            e.Property(x => x.UserName).HasMaxLength(128);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(256);
            e.Property(x => x.ErrorMessage).HasMaxLength(500);
        });
    }
}
