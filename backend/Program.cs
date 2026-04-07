var builder = WebApplication.CreateBuilder(args);
var rooms = new HashSet<string> { "123456", "654321" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("ClientPolicy", policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors("ClientPolicy");

app.MapGet("/api/time", () =>
{
    var unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    return Results.Ok(new { unixSeconds });
});

app.MapGet("/api/rooms", () =>
{
    var items = rooms
        .OrderBy(id => id)
        .Select(id => new { roomId = id });
    return Results.Ok(items);
});

app.MapPost("/api/rooms", () =>
{
    var roomId = GenerateRoomId(rooms);
    rooms.Add(roomId);
    return Results.Ok(new { roomId });
});

app.MapGet("/api/rooms/{roomId}", (string roomId) =>
{
    var isValidFormat = roomId.Length == 6 && roomId.All(char.IsDigit);
    if (!isValidFormat) return Results.BadRequest(new { message = "Room ID must be 6 digits." });
    if (!rooms.Contains(roomId)) return Results.NotFound(new { message = "Room not found." });
    return Results.Ok(new { roomId });
});

app.Run();

static string GenerateRoomId(HashSet<string> rooms)
{
    const int min = 100000;
    const int max = 1000000;
    var attempts = 0;

    while (attempts < 100)
    {
        var roomId = Random.Shared.Next(min, max).ToString();
        if (!rooms.Contains(roomId)) return roomId;
        attempts++;
    }

    throw new InvalidOperationException("Unable to generate unique room id.");
}
