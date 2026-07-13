using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.DataAccess.Migrations
{
    /// <summary>
    /// Priority column (BE-09): optional, case-sensitive 'High' | 'Medium' | 'Low', null valid
    /// (no priority) — nvarchar(10), no default, no check constraint by design (any of the four
    /// states is legal and unvalidated).
    ///
    /// Index change (sort-order enforcement, spec §7 update): every incomplete-items read now
    /// sorts by Priority FIRST (High → Medium → Low → null), then DueDate nulls-last, then
    /// CreateDt. That sort key is a CASE expression over Priority, which SQL Server cannot use
    /// as an index *key* for an ordered scan — so both composite indexes keep their existing
    /// keys, (ListId, IsCompleted, DueDate) and (OwnerUserId, IsCompleted, DueDate), and add
    /// Priority as an INCLUDE column. That keeps the filtered reads covered for computing the
    /// priority rank without a key-lookup per row, without widening the index keys.
    /// Recreating the indexes is metadata + rebuild only; no data is touched by the index steps.
    /// </summary>
    public partial class AddTodoItemPriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TodoItems_ListId_IsCompleted_DueDate",
                table: "TodoItems");

            migrationBuilder.DropIndex(
                name: "IX_TodoItems_OwnerUserId_IsCompleted_DueDate",
                table: "TodoItems");

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "TodoItems",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
                columns: new[] { "ListId", "IsCompleted", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_TodoItems_OwnerUserId_IsCompleted_DueDate",
                table: "TodoItems",
                columns: new[] { "OwnerUserId", "IsCompleted", "DueDate" });
        }
    }
}
