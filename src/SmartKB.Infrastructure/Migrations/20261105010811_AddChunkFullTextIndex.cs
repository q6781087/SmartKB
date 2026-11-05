using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartKB.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkFullTextIndex : Migration
    {
        /// <summary>
        /// kb_chunks 全文检索表达式 GIN 索引（混合检索的关键词路）。
        ///
        /// 用表达式索引而非生成列：EF 模型零改动，不需要 Down 里改表结构。
        /// 检索 SQL 必须使用完全相同的表达式 to_tsvector('simple'::regconfig, "Content") 才能命中索引。
        /// 注意：单参数 to_tsvector(text) 是 STABLE 而非 IMMUTABLE，PostgreSQL 不允许建表达式索引；
        /// 显式转 regconfig 走两参数 IMMUTABLE 版本，这是最常见的踩坑点。
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_kb_chunks_Content_Fts"
                ON kb_chunks USING GIN (to_tsvector('simple'::regconfig, "Content"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_kb_chunks_Content_Fts";
                """);
        }
    }
}
