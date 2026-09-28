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
            e.Property(x => x.Embedding).HasColumnType("vector(1024)");
            e.HasIndex(x => new { x.KbId, x.DocumentId });
            // HNSW 余弦相似度索引（RAG 检索主索引）
            e.HasIndex(x => x.Embedding)
                .HasMethod("hnsw")
                .HasOperators("vector_cosine_ops");
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
    }
}
