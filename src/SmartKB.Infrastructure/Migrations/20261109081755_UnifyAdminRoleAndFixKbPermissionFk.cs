using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartKB.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UnifyAdminRoleAndFixKbPermissionFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---------- 数据修复：必须放在 DDL 之前 ----------

            // 1) 旧实现里 KbId 不是外键、删库也没有级联清理，历史库可能残留
            //    指向已删除知识库的授权记录。不先删掉，下面的 AddForeignKey 会直接失败。
            migrationBuilder.Sql("""
                DELETE FROM "kb_permissions" p
                WHERE NOT EXISTS (
                    SELECT 1 FROM "kb_knowledge_bases" k WHERE k."Id" = p."KbId"
                );
                """);

            // 2) admin 角色与 sys_users.IsAdmin 统一：以 admin 角色为准刷存量错配数据，
            //    避免迁移后仍出现"挂了 admin 角色却被当普通用户"的账号。
            migrationBuilder.Sql("""
                UPDATE "sys_users" u
                SET "IsAdmin" = EXISTS (
                    SELECT 1
                    FROM "sys_user_roles" ur
                    JOIN "sys_roles" r ON r."Id" = ur."RoleId"
                    WHERE ur."UserId" = u."Id" AND r."Name" = 'admin'
                );
                """);

            // KnowledgeBaseId 是 EF 约定生成的影子外键列，业务代码从未写入，恒为 NULL，
            // 删除它不会丢失有效数据。
            migrationBuilder.DropForeignKey(
                name: "FK_kb_permissions_kb_knowledge_bases_KnowledgeBaseId",
                table: "kb_permissions");

            migrationBuilder.DropIndex(
                name: "IX_kb_permissions_KnowledgeBaseId",
                table: "kb_permissions");

            migrationBuilder.DropColumn(
                name: "KnowledgeBaseId",
                table: "kb_permissions");

            migrationBuilder.AddForeignKey(
                name: "FK_kb_permissions_kb_knowledge_bases_KbId",
                table: "kb_permissions",
                column: "KbId",
                principalTable: "kb_knowledge_bases",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_kb_permissions_kb_knowledge_bases_KbId",
                table: "kb_permissions");

            migrationBuilder.AddColumn<long>(
                name: "KnowledgeBaseId",
                table: "kb_permissions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_kb_permissions_KnowledgeBaseId",
                table: "kb_permissions",
                column: "KnowledgeBaseId");

            migrationBuilder.AddForeignKey(
                name: "FK_kb_permissions_kb_knowledge_bases_KnowledgeBaseId",
                table: "kb_permissions",
                column: "KnowledgeBaseId",
                principalTable: "kb_knowledge_bases",
                principalColumn: "Id");
        }
    }
}
