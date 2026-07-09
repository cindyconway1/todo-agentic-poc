using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddVolunteers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Volunteers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LeagueId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreateDt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUpdateDt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreateUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastUpdateUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Volunteers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Volunteers_Leagues_LeagueId",
                        column: x => x.LeagueId,
                        principalTable: "Leagues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Volunteers_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VolunteerTeams",
                columns: table => new
                {
                    VolunteerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerTeams", x => new { x.VolunteerId, x.TeamId });
                    table.ForeignKey(
                        name: "FK_VolunteerTeams_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VolunteerTeams_Volunteers_VolunteerId",
                        column: x => x.VolunteerId,
                        principalTable: "Volunteers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Volunteers_LeagueId",
                table: "Volunteers",
                column: "LeagueId");

            migrationBuilder.CreateIndex(
                name: "IX_Volunteers_OwnerUserId",
                table: "Volunteers",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTeams_TeamId",
                table: "VolunteerTeams",
                column: "TeamId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VolunteerTeams");

            migrationBuilder.DropTable(
                name: "Volunteers");
        }
    }
}
