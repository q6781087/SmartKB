using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartKB.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentToolGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sys_agent_tool_grants",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ToolId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: true),
                    RoleId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sys_agent_tool_grants", x => x.Id);
                    table.CheckConstraint("CK_agent_tool_grants_target", "\"UserId\" IS NOT NULL OR \"RoleId\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_sys_agent_tool_grants_sys_agent_tools_ToolId",
                        column: x => x.ToolId,
                        principalTable: "sys_agent_tools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sys_agent_tool_grants_sys_roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "sys_roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sys_agent_tool_grants_sys_users_UserId",
                        column: x => x.UserId,
                        principalTable: "sys_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sys_agent_tool_grants_RoleId",
                table: "sys_agent_tool_grants",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_sys_agent_tool_grants_ToolId_RoleId",
                table: "sys_agent_tool_grants",
                columns: new[] { "ToolId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sys_agent_tool_grants_ToolId_UserId",
                table: "sys_agent_tool_grants",
                columns: new[] { "ToolId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sys_agent_tool_grants_UserId",
                table: "sys_agent_tool_grants",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sys_agent_tool_grants");
        }
    }
}
