using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EmbeddedAtUtc",
                schema: "catalog",
                table: "SubscriberReportVisualQuestion",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "Embedding",
                schema: "catalog",
                table: "SubscriberReportVisualQuestion",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingModel",
                schema: "catalog",
                table: "SubscriberReportVisualQuestion",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmbeddedAtUtc",
                schema: "catalog",
                table: "SubscriberReportVisualQuestion");

            migrationBuilder.DropColumn(
                name: "Embedding",
                schema: "catalog",
                table: "SubscriberReportVisualQuestion");

            migrationBuilder.DropColumn(
                name: "EmbeddingModel",
                schema: "catalog",
                table: "SubscriberReportVisualQuestion");
        }
    }
}
