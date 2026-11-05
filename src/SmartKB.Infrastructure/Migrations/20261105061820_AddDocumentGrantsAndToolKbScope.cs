using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartKB.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentGrantsAndToolKbScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedKbIdsJson",
                table: "sys_agent_tools",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "kb_document_grants",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: true),
                    RoleId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kb_document_grants", x => x.Id);
                    table.CheckConstraint("CK_document_grants_target", "\"UserId\" IS NOT NULL OR \"RoleId\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_kb_document_grants_kb_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "kb_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_kb_document_grants_sys_roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "sys_roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_kb_document_grants_sys_users_UserId",
                        column: x => x.UserId,
                        principalTable: "sys_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_kb_document_grants_DocumentId_RoleId",
                table: "kb_document_grants",
                columns: new[] { "DocumentId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_kb_document_grants_DocumentId_UserId",
                table: "kb_document_grants",
                columns: new[] { "DocumentId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_kb_document_grants_RoleId",
                table: "kb_document_grants",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_kb_document_grants_UserId",
                table: "kb_document_grants",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kb_document_grants");

            migrationBuilder.DropColumn(
                name: "AllowedKbIdsJson",
                table: "sys_agent_tools");
        }
    }
}
