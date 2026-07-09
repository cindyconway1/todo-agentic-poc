using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.DataAccess.Migrations
{
    /// <summary>
    /// TodoLists table (BE-06): one implicit to-do list per scope entity.
    /// - Unique (ScopeTypeId, ScopeEntityId) index: enforces one list per entity and makes the
    ///   API's get-or-create idempotent under concurrency.
    /// - ScopeEntityId deliberately has NO foreign key: it references Leagues, Teams, or
    ///   Volunteers depending on ScopeTypeId (a polymorphic reference a single FK cannot
    ///   express). Existence + ownership of the referenced entity are enforced at the data
    ///   portal in TodoListEdit.
    /// - The TodoList→TodoItem ON DELETE CASCADE lands with the TodoItem table in BE-07 (the FK
    ///   lives on the item side).
    /// </summary>
    public partial class AddTodoLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TodoLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeTypeId = table.Column<int>(type: "int", nullable: false),
                    ScopeEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreateDt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUpdateDt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreateUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastUpdateUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodoLists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodoLists_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TodoLists_OwnerUserId",
                table: "TodoLists",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TodoLists_ScopeTypeId_ScopeEntityId",
                table: "TodoLists",
                columns: new[] { "ScopeTypeId", "ScopeEntityId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TodoLists");
        }
    }
}
