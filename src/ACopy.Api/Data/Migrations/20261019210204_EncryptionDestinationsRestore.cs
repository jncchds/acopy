using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ACopy.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class EncryptionDestinationsRestore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EncryptionKeyId",
                table: "Targets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxAgeDays",
                table: "Targets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RestoreCommand",
                table: "Targets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EncryptionKeyId",
                table: "Runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Runs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "SourceRunId",
                table: "Runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Destinations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RcloneType = table.Column<string>(type: "TEXT", nullable: true),
                    ParamsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SecretsProtected = table.Column<string>(type: "TEXT", nullable: true),
                    RemotePath = table.Column<string>(type: "TEXT", nullable: true),
                    Keep = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxAgeDays = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Destinations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Keys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    SecretProtected = table.Column<string>(type: "TEXT", nullable: true),
                    Recipients = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Keys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TargetDestinations",
                columns: table => new
                {
                    TargetId = table.Column<int>(type: "INTEGER", nullable: false),
                    DestinationId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetDestinations", x => new { x.TargetId, x.DestinationId });
                    table.ForeignKey(
                        name: "FK_TargetDestinations_Destinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "Destinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TargetDestinations_Targets_TargetId",
                        column: x => x.TargetId,
                        principalTable: "Targets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Uploads",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<long>(type: "INTEGER", nullable: false),
                    DestinationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    RemoteRef = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Uploads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Uploads_Destinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "Destinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Uploads_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Targets_EncryptionKeyId",
                table: "Targets",
                column: "EncryptionKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_Destinations_Name",
                table: "Destinations",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Keys_Name",
                table: "Keys",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TargetDestinations_DestinationId",
                table: "TargetDestinations",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_Uploads_DestinationId_Status",
                table: "Uploads",
                columns: new[] { "DestinationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Uploads_RunId",
                table: "Uploads",
                column: "RunId");

            migrationBuilder.AddForeignKey(
                name: "FK_Targets_Keys_EncryptionKeyId",
                table: "Targets",
                column: "EncryptionKeyId",
                principalTable: "Keys",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Targets_Keys_EncryptionKeyId",
                table: "Targets");

            migrationBuilder.DropTable(
                name: "Keys");

            migrationBuilder.DropTable(
                name: "TargetDestinations");

            migrationBuilder.DropTable(
                name: "Uploads");

            migrationBuilder.DropTable(
                name: "Destinations");

            migrationBuilder.DropIndex(
                name: "IX_Targets_EncryptionKeyId",
                table: "Targets");

            migrationBuilder.DropColumn(
                name: "EncryptionKeyId",
                table: "Targets");

            migrationBuilder.DropColumn(
                name: "MaxAgeDays",
                table: "Targets");

            migrationBuilder.DropColumn(
                name: "RestoreCommand",
                table: "Targets");

            migrationBuilder.DropColumn(
                name: "EncryptionKeyId",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "SourceRunId",
                table: "Runs");
        }
    }
}
