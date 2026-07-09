using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.DataAccess.Migrations
{
    /// <summary>
    /// TodoItems table (BE-07): to-do items with one-way completion and due-date sorting.
    /// - ListId FK → TodoLists with ON DELETE CASCADE (deleting a list removes its items,
    ///   spec §3 delete behavior); OwnerUserId FK → Users is RESTRICT because
    ///   Users→TodoItems plus Users→TodoLists→TodoItems(CASCADE) would be multiple cascade paths.
    /// - OwnerUserId is denormalized from the owning list so the All-Items query (BE-08) never
    ///   joins through TodoLists.
    /// - Both composite indexes lead with their FK column, so they double as the FK indexes:
    ///   (ListId, IsCompleted, DueDate) serves the per-list incomplete-items query and
    ///   (OwnerUserId, IsCompleted, DueDate) serves All-Items — every read filters
    ///   IsCompleted = 0 and sorts by DueDate.
    /// </summary>
    public partial class AddTodoItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TodoItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IsCompleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreateDt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUpdateDt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreateUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastUpdateUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodoItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodoItems_TodoLists_ListId",
                        column: x => x.ListId,
                        principalTable: "TodoLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TodoItems_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TodoItems_ListId_IsCompleted_DueDate",
                table: "TodoItems",
                columns: new[] { "ListId", "IsCompleted", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_TodoItems_OwnerUserId_IsCompleted_DueDate",
                table: "TodoItems",
                columns: new[] { "OwnerUserId", "IsCompleted", "DueDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TodoItems");
        }
    }
}
