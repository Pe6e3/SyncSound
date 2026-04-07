using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var rooms = new HashSet<string> { "123456", "654321" };
var roomDevices = rooms.ToDictionary(roomId => roomId, _ => new List<DeviceEntry>());
var roomAudioStates = rooms.ToDictionary(roomId => roomId, _ => new RoomAudioState(null, 0, null));
var syncRoot = new object();
var versionFilePath = Environment.GetEnvironmentVariable("VERSION_FILE_PATH") ?? "/app/data/version.json";
var audioBaseDirectory = Environment.GetEnvironmentVariable("AUDIO_STORAGE_PATH") ?? "/app/data/audio";
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
    lock (syncRoot)
    {
        var items = rooms
            .OrderBy(id => id)
            .Select(id =>
            {
                var count = roomDevices.TryGetValue(id, out var devices) ? devices.Count : 0;
                return new { roomId = id, deviceCount = count };
            });
        return Results.Ok(items);
    }
});

app.MapPost("/api/rooms", () =>
{
    var roomId = GenerateRoomId(rooms);
    rooms.Add(roomId);
    roomDevices[roomId] = new List<DeviceEntry>();
    roomAudioStates[roomId] = new RoomAudioState(null, 0, null);
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
        return Results.Ok(BuildRoomResponse(roomId, devices, roomAudioStates));
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
        if (!roomAudioStates.ContainsKey(roomId)) roomAudioStates[roomId] = new RoomAudioState(null, 0, null);

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
            return Results.Ok(new RegisterDeviceResponse(deviceId, BuildRoomResponse(roomId, devices, roomAudioStates)));
        }

        var updatedDevice = existingDevice with
        {
            DisplayName = NormalizeDisplayName(request.DisplayName) ?? existingDevice.DisplayName,
            LastSeenUtc = nowUnix,
            DeviceInfo = normalizedDeviceInfo
        };
        ReplaceDevice(devices, updatedDevice);
        return Results.Ok(new RegisterDeviceResponse(updatedDevice.DeviceId, BuildRoomResponse(roomId, devices, roomAudioStates)));
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

        return Results.Ok(BuildRoomResponse(roomId, devices, roomAudioStates));
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

        return Results.Ok(BuildRoomResponse(roomId, devices, roomAudioStates));
    }
});

app.MapPost("/api/rooms/{roomId}/audio", async (string roomId, HttpRequest request) =>
{
    if (!IsValidRoomId(roomId)) return Results.BadRequest(new { message = "Room ID must be 6 digits." });
    if (!rooms.Contains(roomId)) return Results.NotFound(new { message = "Room not found." });

    var form = await request.ReadFormAsync();
    var actorDeviceId = form["actorDeviceId"].ToString();
    var file = form.Files.GetFile("file");

    if (!IsValidDeviceId(actorDeviceId)) return Results.BadRequest(new { message = "Actor device ID is invalid." });
    if (file is null || file.Length == 0) return Results.BadRequest(new { message = "Audio file is required." });

    lock (syncRoot)
    {
        if (!roomDevices.TryGetValue(roomId, out var devices)) return Results.NotFound(new { message = "Room not found." });
        var actorDevice = devices.FirstOrDefault(device => device.DeviceId == actorDeviceId);
        if (actorDevice is null) return Results.NotFound(new { message = "Actor device not found in room." });
        if (!actorDevice.IsMaster) return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var extension = Path.GetExtension(file.FileName);
    if (string.IsNullOrWhiteSpace(extension) || extension.Length > 10) extension = ".bin";

    RoomAudioState updatedAudioState;
    var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    string outputPath;

    lock (syncRoot)
    {
        if (!roomAudioStates.TryGetValue(roomId, out var currentAudio)) currentAudio = new RoomAudioState(null, 0, null);
        var nextRevision = currentAudio.Revision + 1;
        var fileName = $"audio-r{nextRevision}{extension.ToLowerInvariant()}";
        var roomDirectory = Path.Combine(audioBaseDirectory, roomId);
        Directory.CreateDirectory(roomDirectory);

        foreach (var path in Directory.GetFiles(roomDirectory)) File.Delete(path);

        outputPath = Path.Combine(roomDirectory, fileName);
        updatedAudioState = new RoomAudioState(fileName, nextRevision, nowUnix);
        roomAudioStates[roomId] = updatedAudioState;
    }

    using (var stream = File.Create(outputPath))
    {
        await file.CopyToAsync(stream);
    }

    lock (syncRoot)
    {
        var devices = roomDevices.TryGetValue(roomId, out var value) ? value : new List<DeviceEntry>();
        return Results.Ok(BuildRoomResponse(roomId, devices, roomAudioStates));
    }
});

app.MapGet("/api/rooms/{roomId}/audio", (string roomId) =>
{
    if (!IsValidRoomId(roomId)) return Results.BadRequest(new { message = "Room ID must be 6 digits." });
    if (!rooms.Contains(roomId)) return Results.NotFound(new { message = "Room not found." });

    RoomAudioState audioState;
    lock (syncRoot)
    {
        if (!roomAudioStates.TryGetValue(roomId, out audioState!)) return Results.NotFound(new { message = "Audio not found." });
    }

    if (string.IsNullOrWhiteSpace(audioState.FileName)) return Results.NotFound(new { message = "Audio not found." });

    var path = Path.Combine(audioBaseDirectory, roomId, audioState.FileName);
    if (!File.Exists(path)) return Results.NotFound(new { message = "Audio not found." });

    var contentType = GetContentTypeByExtension(Path.GetExtension(path));
    return Results.File(path, contentType, audioState.FileName);
});

static RoomDetailsResponse BuildRoomResponse(
    string roomId,
    List<DeviceEntry> devices,
    Dictionary<string, RoomAudioState> roomAudioStates
)
{
    var mappedDevices = devices.Select(MapDevice).ToList();
    roomAudioStates.TryGetValue(roomId, out var audioState);
    var audio = MapRoomAudio(audioState);
    return new RoomDetailsResponse(roomId, mappedDevices, audio);
}

static RoomAudioResponse MapRoomAudio(RoomAudioState? audioState)
{
    if (audioState is null || string.IsNullOrWhiteSpace(audioState.FileName))
        return new RoomAudioResponse(false, null, 0, null);

    return new RoomAudioResponse(true, audioState.FileName, audioState.Revision, audioState.UpdatedAtUtc);
}

static string GetContentTypeByExtension(string extension)
{
    return extension.ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        ".aac" => "audio/aac",
        ".m4a" => "audio/mp4",
        ".flac" => "audio/flac",
        _ => "application/octet-stream"
    };
}

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
record RoomAudioState(string? FileName, long Revision, long? UpdatedAtUtc);

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
    [property: JsonPropertyName("devices")] List<DeviceResponse> Devices,
    [property: JsonPropertyName("audio")] RoomAudioResponse Audio
);

record RoomAudioResponse(
    [property: JsonPropertyName("hasAudio")] bool HasAudio,
    [property: JsonPropertyName("fileName")] string? FileName,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("updatedAtUtc")] long? UpdatedAtUtc
);

record DeviceResponse(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("firstSeenUtc")] long FirstSeenUtc,
    [property: JsonPropertyName("lastSeenUtc")] long LastSeenUtc,
    [property: JsonPropertyName("deviceInfo")] Dictionary<string, string> DeviceInfo,
    [property: JsonPropertyName("isMaster")] bool IsMaster
);
