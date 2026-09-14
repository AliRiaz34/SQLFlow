using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionExampleConfirmedFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Confirmed",
                schema: "catalog",
                table: "QuestionExample",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionNote",
                schema: "catalog",
                table: "QuestionExample",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuestionExample_Confirmed",
                schema: "catalog",
                table: "QuestionExample",
                column: "Confirmed");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QuestionExample_Confirmed",
                schema: "catalog",
                table: "QuestionExample");

            migrationBuilder.DropColumn(
                name: "RejectionNote",
                schema: "catalog",
                table: "QuestionExample");

            migrationBuilder.DropColumn(
                name: "Confirmed",
                schema: "catalog",
                table: "QuestionExample");
        }
    }
}
