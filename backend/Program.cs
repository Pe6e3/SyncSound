using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var rooms = new HashSet<string> { "123456", "654321" };
var roomDevices = rooms.ToDictionary(roomId => roomId, _ => new List<DeviceEntry>());
var syncRoot = new object();
var versionFilePath = Environment.GetEnvironmentVariable("VERSION_FILE_PATH") ?? "/app/data/version.json";
EnsureVersionFile(versionFilePath);

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

app.MapGet("/api/version", () =>
{
    var version = ReadVersion(versionFilePath);
    return Results.Ok(new { version });
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
    roomDevices[roomId] = new List<DeviceEntry>();
    return Results.Ok(new { roomId });
});

app.MapGet("/api/rooms/{roomId}", (string roomId) =>
{
    var isValidFormat = IsValidRoomId(roomId);
    if (!isValidFormat) return Results.BadRequest(new { message = "Room ID must be 6 digits." });
    if (!rooms.Contains(roomId)) return Results.NotFound(new { message = "Room not found." });

    lock (syncRoot)
    {
        var devices = roomDevices.TryGetValue(roomId, out var value) ? value : new List<DeviceEntry>();
        return Results.Ok(new RoomDetailsResponse(roomId, devices.Select(MapDevice).ToList()));
    }
});

app.MapPost("/api/rooms/{roomId}/devices/register", (string roomId, RegisterDeviceRequest request) =>
{
    if (!IsValidRoomId(roomId)) return Results.BadRequest(new { message = "Room ID must be 6 digits." });

    var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var normalizedDeviceInfo = NormalizeDeviceInfo(request.DeviceInfo);

    lock (syncRoot)
    {
        if (!rooms.Contains(roomId)) rooms.Add(roomId);

        if (!roomDevices.TryGetValue(roomId, out var devices))
        {
            devices = new List<DeviceEntry>();
            roomDevices[roomId] = devices;
        }

        var existingDevice = devices.FirstOrDefault(device => device.DeviceId == request.DeviceId);
        if (existingDevice is null)
        {
            var deviceId = IsValidDeviceId(request.DeviceId) ? request.DeviceId! : GenerateDeviceId(devices);
            var newDevice = new DeviceEntry(
                deviceId,
                NormalizeDisplayName(request.DisplayName),
                nowUnix,
                nowUnix,
                normalizedDeviceInfo,
                !devices.Any(device => device.IsMaster)
            );
            devices.Add(newDevice);
            return Results.Ok(new RegisterDeviceResponse(deviceId, new RoomDetailsResponse(roomId, devices.Select(MapDevice).ToList())));
        }

        var updatedDevice = existingDevice with
        {
            DisplayName = NormalizeDisplayName(request.DisplayName) ?? existingDevice.DisplayName,
            LastSeenUtc = nowUnix,
            DeviceInfo = normalizedDeviceInfo
        };
        ReplaceDevice(devices, updatedDevice);
        return Results.Ok(new RegisterDeviceResponse(updatedDevice.DeviceId, new RoomDetailsResponse(roomId, devices.Select(MapDevice).ToList())));
    }
});

app.MapPatch("/api/rooms/{roomId}/devices/{deviceId}/name", (string roomId, string deviceId, UpdateDeviceNameRequest request) =>
{
    if (!IsValidRoomId(roomId)) return Results.BadRequest(new { message = "Room ID must be 6 digits." });
    if (!rooms.Contains(roomId)) return Results.NotFound(new { message = "Room not found." });
    if (!IsValidDeviceId(deviceId)) return Results.BadRequest(new { message = "Device ID is invalid." });

    lock (syncRoot)
    {
        if (!roomDevices.TryGetValue(roomId, out var devices)) return Results.NotFound(new { message = "Room not found." });
        var existingDevice = devices.FirstOrDefault(device => device.DeviceId == deviceId);
        if (existingDevice is null) return Results.NotFound(new { message = "Device not found in room." });

        var updatedDevice = existingDevice with
        {
            DisplayName = NormalizeDisplayName(request.DisplayName),
            LastSeenUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        ReplaceDevice(devices, updatedDevice);

        return Results.Ok(new RoomDetailsResponse(roomId, devices.Select(MapDevice).ToList()));
    }
});

app.MapPost("/api/rooms/{roomId}/devices/{deviceId}/master", (string roomId, string deviceId, ChangeMasterRequest request) =>
{
    if (!IsValidRoomId(roomId)) return Results.BadRequest(new { message = "Room ID must be 6 digits." });
    if (!rooms.Contains(roomId)) return Results.NotFound(new { message = "Room not found." });
    if (!IsValidDeviceId(deviceId)) return Results.BadRequest(new { message = "Device ID is invalid." });
    if (!IsValidDeviceId(request.ActorDeviceId)) return Results.BadRequest(new { message = "Actor device ID is invalid." });

    lock (syncRoot)
    {
        if (!roomDevices.TryGetValue(roomId, out var devices)) return Results.NotFound(new { message = "Room not found." });
        var actorDevice = devices.FirstOrDefault(device => device.DeviceId == request.ActorDeviceId);
        if (actorDevice is null) return Results.NotFound(new { message = "Actor device not found in room." });
        if (!actorDevice.IsMaster) return Results.StatusCode(StatusCodes.Status403Forbidden);

        var targetDevice = devices.FirstOrDefault(device => device.DeviceId == deviceId);
        if (targetDevice is null) return Results.NotFound(new { message = "Target device not found in room." });

        for (var index = 0; index < devices.Count; index++)
        {
            var isTarget = devices[index].DeviceId == deviceId;
            devices[index] = devices[index] with { IsMaster = isTarget };
        }

        return Results.Ok(new RoomDetailsResponse(roomId, devices.Select(MapDevice).ToList()));
    }
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

static bool IsValidRoomId(string roomId)
{
    return roomId.Length == 6 && roomId.All(char.IsDigit);
}

static bool IsValidDeviceId(string? deviceId)
{
    if (string.IsNullOrWhiteSpace(deviceId)) return false;
    if (deviceId.Length is < 8 or > 36) return false;
    return deviceId.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');
}

static string GenerateDeviceId(List<DeviceEntry> devices)
{
    while (true)
    {
        var deviceId = Guid.NewGuid().ToString("N")[..12];
        if (!devices.Any(device => device.DeviceId == deviceId)) return deviceId;
    }
}

static Dictionary<string, string> NormalizeDeviceInfo(Dictionary<string, string>? input)
{
    if (input is null) return new Dictionary<string, string>();
    return input
        .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
        .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value?.Trim() ?? string.Empty);
}

static string? NormalizeDisplayName(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    var trimmed = value.Trim();
    return trimmed.Length > 50 ? trimmed[..50] : trimmed;
}

static void ReplaceDevice(List<DeviceEntry> devices, DeviceEntry updatedDevice)
{
    var index = devices.FindIndex(device => device.DeviceId == updatedDevice.DeviceId);
    if (index < 0) return;
    devices[index] = updatedDevice;
}

static DeviceResponse MapDevice(DeviceEntry device)
{
    return new DeviceResponse(
        device.DeviceId,
        device.DisplayName,
        device.FirstSeenUtc,
        device.LastSeenUtc,
        device.DeviceInfo,
        device.IsMaster
    );
}

static void EnsureVersionFile(string filePath)
{
    var directoryPath = Path.GetDirectoryName(filePath);
    if (!string.IsNullOrWhiteSpace(directoryPath)) Directory.CreateDirectory(directoryPath);
    if (File.Exists(filePath)) return;

    var payload = JsonSerializer.Serialize(new VersionPayload("v1.0.0"));
    File.WriteAllText(filePath, payload);
}

static string ReadVersion(string filePath)
{
    try
    {
        var json = File.ReadAllText(filePath);
        var payload = JsonSerializer.Deserialize<VersionPayload>(json);
        if (!string.IsNullOrWhiteSpace(payload?.Version)) return payload.Version;
    }
    catch
    {
        // Fallback handled below.
    }

    return "v1.0.0";
}

record VersionPayload([property: JsonPropertyName("version")] string Version);

record RegisterDeviceRequest(
    [property: JsonPropertyName("deviceId")] string? DeviceId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("deviceInfo")] Dictionary<string, string>? DeviceInfo
);

record UpdateDeviceNameRequest([property: JsonPropertyName("displayName")] string? DisplayName);
record ChangeMasterRequest([property: JsonPropertyName("actorDeviceId")] string ActorDeviceId);

record DeviceEntry(
    string DeviceId,
    string? DisplayName,
    long FirstSeenUtc,
    long LastSeenUtc,
    Dictionary<string, string> DeviceInfo,
    bool IsMaster
);

record RegisterDeviceResponse(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("room")] RoomDetailsResponse Room
);

record RoomDetailsResponse(
    [property: JsonPropertyName("roomId")] string RoomId,
    [property: JsonPropertyName("devices")] List<DeviceResponse> Devices
);

record DeviceResponse(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("firstSeenUtc")] long FirstSeenUtc,
    [property: JsonPropertyName("lastSeenUtc")] long LastSeenUtc,
    [property: JsonPropertyName("deviceInfo")] Dictionary<string, string> DeviceInfo,
    [property: JsonPropertyName("isMaster")] bool IsMaster
);
