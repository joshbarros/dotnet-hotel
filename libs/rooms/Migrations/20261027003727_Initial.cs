using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hotel.Rooms.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "rooms");

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                schema: "rooms",
                columns: table => new
                {
                    Consumer = table.Column<string>(type: "text", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => new { x.Consumer, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "rooms",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Module = table.Column<string>(type: "text", nullable: false),
                    RoutingKey = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoomTypes",
                schema: "rooms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    City = table.Column<string>(type: "text", nullable: false),
                    TotalRooms = table.Column<int>(type: "integer", nullable: false),
                    RateCents = table.Column<long>(type: "bigint", nullable: false),
                    FirstNight = table.Column<DateOnly>(type: "date", nullable: false),
                    OpenNights = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoomHolds",
                schema: "rooms",
                columns: table => new
                {
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckIn = table.Column<DateOnly>(type: "date", nullable: false),
                    Nights = table.Column<int>(type: "integer", nullable: false),
                    Rooms = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleaseReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomHolds", x => x.BookingId);
                    table.ForeignKey(
                        name: "FK_RoomHolds_RoomTypes_RoomTypeId",
                        column: x => x.RoomTypeId,
                        principalSchema: "rooms",
                        principalTable: "RoomTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RoomNights",
                schema: "rooms",
                columns: table => new
                {
                    RoomTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Night = table.Column<DateOnly>(type: "date", nullable: false),
                    TotalRooms = table.Column<int>(type: "integer", nullable: false),
                    RoomsLeft = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomNights", x => new { x.RoomTypeId, x.Night });
                    table.CheckConstraint("CK_RoomNights_Remaining", "\"RoomsLeft\" >= 0 AND \"RoomsLeft\" <= \"TotalRooms\"");
                    table.ForeignKey(
                        name: "FK_RoomNights_RoomTypes_RoomTypeId",
                        column: x => x.RoomTypeId,
                        principalSchema: "rooms",
                        principalTable: "RoomTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_SentAt",
                schema: "rooms",
                table: "OutboxMessages",
                column: "SentAt",
                filter: "\"SentAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RoomHolds_RoomTypeId",
                schema: "rooms",
                table: "RoomHolds",
                column: "RoomTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_RoomTypes_Code",
                schema: "rooms",
                table: "RoomTypes",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxMessages",
                schema: "rooms");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "rooms");

            migrationBuilder.DropTable(
                name: "RoomHolds",
                schema: "rooms");

            migrationBuilder.DropTable(
                name: "RoomNights",
                schema: "rooms");

            migrationBuilder.DropTable(
                name: "RoomTypes",
                schema: "rooms");
        }
    }
}
