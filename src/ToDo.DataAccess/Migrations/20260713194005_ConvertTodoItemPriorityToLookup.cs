using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ToDo.DataAccess.Migrations
{
    /// <summary>
    /// Priority string → lookup table (BE-10). Order matters: the Priorities table is created
    /// and seeded and the nullable PriorityId column added FIRST, then the backfill maps the
    /// legacy strings ('High'→1, 'Medium'→2, 'Low'→3, anything else → NULL) while both columns
    /// coexist, and only then is the old Priority column dropped and the FK added. The two
    /// composite indexes swap their INCLUDE column from Priority to PriorityId; the §7 sort is
    /// now a join on Priorities.SortOrder instead of a CASE over the string.
    /// </summary>
    public partial class ConvertTodoItemPriorityToLookup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Priorities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Priorities", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Priorities",
                columns: new[] { "Id", "Name", "SortOrder" },
                values: new object[,]
                {
                    { 1, "High", 1 },
                    { 2, "Medium", 2 },
                    { 3, "Low", 3 }
                });

            migrationBuilder.AddColumn<int>(
                name: "PriorityId",
                table: "TodoItems",
                type: "int",
                nullable: true);

            // Backfill while the string column still exists. The join is on the seeded names
            // with a case-sensitive collation (the contract values were case-sensitive), so an
            // unrecognized or miscased legacy value simply stays NULL (valid: no priority).
            migrationBuilder.Sql(
                """
                UPDATE i
                SET i.PriorityId = p.Id
                FROM TodoItems AS i
                INNER JOIN Priorities AS p ON p.Name = i.Priority COLLATE SQL_Latin1_General_CP1_CS_AS;
                """);

            migrationBuilder.DropIndex(
                name: "IX_TodoItems_ListId_IsCompleted_DueDate",
                table: "TodoItems");

            migrationBuilder.DropIndex(
                name: "IX_TodoItems_OwnerUserId_IsCompleted_DueDate",
                table: "TodoItems");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "TodoItems");

            migrationBuilder.CreateIndex(
                name: "IX_TodoItems_ListId_IsCompleted_DueDate",
                table: "TodoItems",
                columns: new[] { "ListId", "IsCompleted", "DueDate" })
                .Annotation("SqlServer:Include", new[] { "PriorityId" });

            migrationBuilder.CreateIndex(
                name: "IX_TodoItems_OwnerUserId_IsCompleted_DueDate",
                table: "TodoItems",
                columns: new[] { "OwnerUserId", "IsCompleted", "DueDate" })
                .Annotation("SqlServer:Include", new[] { "PriorityId" });

            migrationBuilder.CreateIndex(
                name: "IX_TodoItems_PriorityId",
                table: "TodoItems",
                column: "PriorityId");

            migrationBuilder.AddForeignKey(
                name: "FK_TodoItems_Priorities_PriorityId",
                table: "TodoItems",
                column: "PriorityId",
                principalTable: "Priorities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "TodoItems",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            // Restore the legacy strings from the lookup before the FK/table go away.
            migrationBuilder.Sql(
                """
                UPDATE i
                SET i.Priority = p.Name
                FROM TodoItems AS i
                INNER JOIN Priorities AS p ON p.Id = i.PriorityId;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_TodoItems_Priorities_PriorityId",
                table: "TodoItems");

            migrationBuilder.DropTable(
                name: "Priorities");

            migrationBuilder.DropIndex(
                name: "IX_TodoItems_ListId_IsCompleted_DueDate",
                table: "TodoItems");

            migrationBuilder.DropIndex(
                name: "IX_TodoItems_OwnerUserId_IsCompleted_DueDate",
                table: "TodoItems");

            migrationBuilder.DropIndex(
                name: "IX_TodoItems_PriorityId",
                table: "TodoItems");

            migrationBuilder.DropColumn(
                name: "PriorityId",
                table: "TodoItems");

            migrationBuilder.CreateIndex(
                name: "IX_TodoItems_ListId_IsCompleted_DueDate",
                table: "TodoItems",
                columns: new[] { "ListId", "IsCompleted", "DueDate" })
                .Annotation("SqlServer:Include", new[] { "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_TodoItems_OwnerUserId_IsCompleted_DueDate",
                table: "TodoItems",
                columns: new[] { "OwnerUserId", "IsCompleted", "DueDate" })
                .Annotation("SqlServer:Include", new[] { "Priority" });
        }
    }
}
