using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartKB.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sys_agent_tools",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    HttpMethod = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    UrlTemplate = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    BodyTemplate = table.Column<string>(type: "text", nullable: true),
                    ParametersJson = table.Column<string>(type: "text", nullable: false),
                    AuthHeaderName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AuthEnvVar = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AuthValuePrefix = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxResponseChars = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sys_agent_tools", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sys_agent_tools_Enabled",
                table: "sys_agent_tools",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_sys_agent_tools_Name",
                table: "sys_agent_tools",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sys_agent_tools");
        }
    }
}
