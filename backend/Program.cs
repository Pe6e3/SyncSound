using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.WebSockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var rooms = new HashSet<string> { "123456", "654321" };
var roomDevices = rooms.ToDictionary(roomId => roomId, _ => new List<DeviceEntry>());
var roomAudioStates = rooms.ToDictionary(roomId => roomId, _ => new RoomAudioState(null, 0, null));
var roomSockets = new Dictionary<string, Dictionary<string, WebSocket>>();
var soundHomeSockets = new Dictionary<string, WebSocket>();
var roomsCalibrationLocked = new HashSet<string>();
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
app.UseWebSockets();

app.MapGet("/api/time", () =>
{
    var unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    return Results.Ok(new { unixSeconds });
});

app.MapGet("/api/time-ms", () =>
{
    var unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    return Results.Ok(new { unixMs });
});

app.MapGet("/api/version", () =>
{
    var version = ReadVersion(versionFilePath);
    return Results.Ok(new { version });
});

app.MapGet("/api/audio/click", () =>
{
    var clickPath = Path.Combine(audioBaseDirectory, "click.wav");
    if (!File.Exists(clickPath)) return Results.NotFound(new { message = "Click sound not found." });
    return Results.File(clickPath, "audio/wav", "click.wav");
});

app.MapGet("/api/rooms", () =>
{
    lock (syncRoot)
    {
        return Results.Ok(BuildRoomsListResponse(rooms, roomDevices, roomsCalibrationLocked));
    }
});

app.MapPost("/api/rooms", async () =>
{
    var roomId = GenerateRoomId(rooms);
    lock (syncRoot)
    {
        rooms.Add(roomId);
        roomDevices[roomId] = new List<DeviceEntry>();
        roomAudioStates[roomId] = new RoomAudioState(null, 0, null);
    }

    await BroadcastSoundHomeState(soundHomeSockets, rooms, roomDevices, roomsCalibrationLocked, syncRoot);
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
        return Results.Ok(BuildRoomResponse(roomId, devices, roomAudioStates, roomsCalibrationLocked));
    }
});

app.MapPost("/api/rooms/{roomId}/devices/register", async (string roomId, RegisterDeviceRequest request) =>
{
    if (!IsValidRoomId(roomId)) return Results.BadRequest(new { message = "Room ID must be 6 digits." });

    var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var normalizedDeviceInfo = NormalizeDeviceInfo(request.DeviceInfo);
    RegisterDeviceResponse response;

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
        if (existingDevice is null && roomsCalibrationLocked.Contains(roomId))
            return Results.Json(
                new { message = "Комната временно закрыта для входа: идёт калибровка синхронизации." },
                statusCode: StatusCodes.Status403Forbidden);

        if (existingDevice is null)
        {
            var deviceId = IsValidDeviceId(request.DeviceId) ? request.DeviceId! : GenerateDeviceId(devices);
            var newDevice = new DeviceEntry(
                deviceId,
                NormalizeDisplayName(request.DisplayName),
                nowUnix,
                nowUnix,
                normalizedDeviceInfo,
                !devices.Any(device => device.IsMaster),
                false,
                0,
                false,
                0,
                0
            );
            devices.Add(newDevice);
            response = new RegisterDeviceResponse(deviceId, BuildRoomResponse(roomId, devices, roomAudioStates, roomsCalibrationLocked));
        }
        else
        {
            var updatedDevice = existingDevice with
            {
                DisplayName = NormalizeDisplayName(request.DisplayName) ?? existingDevice.DisplayName,
                LastSeenUtc = nowUnix,
                DeviceInfo = normalizedDeviceInfo
            };
            ReplaceDevice(devices, updatedDevice);
            response = new RegisterDeviceResponse(updatedDevice.DeviceId, BuildRoomResponse(roomId, devices, roomAudioStates, roomsCalibrationLocked));
        }
    }

    await BroadcastRoomState(roomId, response.Room, roomSockets, syncRoot);
    await BroadcastSoundHomeState(soundHomeSockets, rooms, roomDevices, roomsCalibrationLocked, syncRoot);
    return Results.Ok(response);
});

app.MapPatch("/api/rooms/{roomId}/devices/{deviceId}/name", async (string roomId, string deviceId, UpdateDeviceNameRequest request) =>
{
    if (!IsValidRoomId(roomId)) return Results.BadRequest(new { message = "Room ID must be 6 digits." });
    if (!rooms.Contains(roomId)) return Results.NotFound(new { message = "Room not found." });
    if (!IsValidDeviceId(deviceId)) return Results.BadRequest(new { message = "Device ID is invalid." });
    RoomDetailsResponse room;

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
        room = BuildRoomResponse(roomId, devices, roomAudioStates, roomsCalibrationLocked);
    }

    await BroadcastRoomState(roomId, room, roomSockets, syncRoot);
    return Results.Ok(room);
});

app.MapPost("/api/rooms/{roomId}/devices/{deviceId}/master", async (string roomId, string deviceId, ChangeMasterRequest request) =>
{
    if (!IsValidRoomId(roomId)) return Results.BadRequest(new { message = "Room ID must be 6 digits." });
    if (!rooms.Contains(roomId)) return Results.NotFound(new { message = "Room not found." });
    if (!IsValidDeviceId(deviceId)) return Results.BadRequest(new { message = "Device ID is invalid." });
    if (!IsValidDeviceId(request.ActorDeviceId)) return Results.BadRequest(new { message = "Actor device ID is invalid." });
    RoomDetailsResponse room;

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
        room = BuildRoomResponse(roomId, devices, roomAudioStates, roomsCalibrationLocked);
    }

    await BroadcastRoomState(roomId, room, roomSockets, syncRoot);
    return Results.Ok(room);
});

app.MapPost("/api/rooms/{roomId}/devices/{deviceId}/audio-ready", async (string roomId, string deviceId, AudioReadyRequest request) =>
{
    if (!IsValidRoomId(roomId)) return Results.BadRequest(new { message = "Room ID must be 6 digits." });
    if (!rooms.Contains(roomId)) return Results.NotFound(new { message = "Room not found." });
    if (!IsValidDeviceId(deviceId)) return Results.BadRequest(new { message = "Device ID is invalid." });
    RoomDetailsResponse room;

    lock (syncRoot)
    {
        if (!roomDevices.TryGetValue(roomId, out var devices)) return Results.NotFound(new { message = "Room not found." });
        var target = devices.FirstOrDefault(device => device.DeviceId == deviceId);
        if (target is null) return Results.NotFound(new { message = "Device not found in room." });

        var updated = target with
        {
            AudioReadyRevision = Math.Max(target.AudioReadyRevision, request.Revision),
            LastSeenUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        ReplaceDevice(devices, updated);
        room = BuildRoomResponse(roomId, devices, roomAudioStates, roomsCalibrationLocked);
    }

    await BroadcastRoomState(roomId, room, roomSockets, syncRoot);
    return Results.Ok(room);
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
    if (!IsAllowedAudioUpload(file)) return Results.BadRequest(new { message = "Only valid audio files are allowed." });

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
        var devices = roomDevices.TryGetValue(roomId, out var value) ? value : new List<DeviceEntry>();
        var nextRevision = currentAudio.Revision + 1;
        var fileName = $"audio-r{nextRevision}{extension.ToLowerInvariant()}";
        var roomDirectory = Path.Combine(audioBaseDirectory, roomId);
        Directory.CreateDirectory(roomDirectory);

        foreach (var path in Directory.GetFiles(roomDirectory)) File.Delete(path);

        outputPath = Path.Combine(roomDirectory, fileName);
        updatedAudioState = new RoomAudioState(fileName, nextRevision, nowUnix);
        roomAudioStates[roomId] = updatedAudioState;

        for (var index = 0; index < devices.Count; index++)
            devices[index] = devices[index] with
            {
                AudioReadyRevision = 0,
                IsPlaybackSyncCalibrated = false,
                PlaybackSyncLagMs = 0,
                PlaybackSyncRevision = 0
            };
    }

    using (var stream = File.Create(outputPath))
    {
        await file.CopyToAsync(stream);
    }

    RoomDetailsResponse room;
    lock (syncRoot)
    {
        var devices = roomDevices.TryGetValue(roomId, out var value) ? value : new List<DeviceEntry>();
        room = BuildRoomResponse(roomId, devices, roomAudioStates, roomsCalibrationLocked);
    }

    await BroadcastRoomState(roomId, room, roomSockets, syncRoot);
    return Results.Ok(room);
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

app.Map("/ws/rooms/{roomId}", async (HttpContext context, string roomId) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var deviceId = context.Request.Query["deviceId"].ToString();
    if (!IsValidRoomId(roomId) || !IsValidDeviceId(deviceId))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    WebSocket socket;
    RoomDetailsResponse? roomSnapshot;

    lock (syncRoot)
    {
        if (!rooms.Contains(roomId) || !roomDevices.TryGetValue(roomId, out var devices))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var device = devices.FirstOrDefault(entry => entry.DeviceId == deviceId);
        if (device is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
    }

    socket = await context.WebSockets.AcceptWebSocketAsync();

    lock (syncRoot)
    {
        var devices = roomDevices[roomId];
        var device = devices.First(entry => entry.DeviceId == deviceId);
        ReplaceDevice(devices, device with { IsOnline = true, LastSeenUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

        if (!roomSockets.TryGetValue(roomId, out var socketsByDevice))
        {
            socketsByDevice = new Dictionary<string, WebSocket>();
            roomSockets[roomId] = socketsByDevice;
        }
        socketsByDevice[deviceId] = socket;
        roomSnapshot = BuildRoomResponse(roomId, devices, roomAudioStates, roomsCalibrationLocked);
    }

    await BroadcastRoomState(roomId, roomSnapshot!, roomSockets, syncRoot);

    var buffer = new byte[2048];
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text) continue;

            var incomingText = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (string.IsNullOrWhiteSpace(incomingText)) continue;

            string? messageType;
            try
            {
                using var document = JsonDocument.Parse(incomingText);
                messageType = document.RootElement.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString()
                    : null;
            }
            catch
            {
                continue;
            }

            if (messageType == "confirm-audio-ready")
            {
                RevisionAudioCommandMessage? confirmPayload;
                try
                {
                    confirmPayload = JsonSerializer.Deserialize<RevisionAudioCommandMessage>(incomingText);
                }
                catch
                {
                    confirmPayload = null;
                }
                if (confirmPayload is null || confirmPayload.Revision <= 0) continue;

                RoomDetailsResponse? roomAfterConfirm = null;
                lock (syncRoot)
                {
                    if (roomDevices.TryGetValue(roomId, out var devices) &&
                        roomAudioStates.TryGetValue(roomId, out var audioState) &&
                        !string.IsNullOrWhiteSpace(audioState.FileName) &&
                        audioState.Revision == confirmPayload.Revision)
                    {
                        var target = devices.FirstOrDefault(entry => entry.DeviceId == deviceId);
                        if (target is not null)
                        {
                            var updated = target with
                            {
                                AudioReadyRevision = Math.Max(target.AudioReadyRevision, confirmPayload.Revision),
                                LastSeenUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            };
                            ReplaceDevice(devices, updated);
                            roomAfterConfirm = BuildRoomResponse(roomId, devices, roomAudioStates, roomsCalibrationLocked);
                        }
                    }
                }

                if (roomAfterConfirm is not null)
                {
                    await BroadcastRoomState(roomId, roomAfterConfirm, roomSockets, syncRoot);
                    await BroadcastSoundHomeState(soundHomeSockets, rooms, roomDevices, roomsCalibrationLocked, syncRoot);
                }
                continue;
            }

            if (messageType == "start-calibration")
            {
                RoomDetailsResponse? roomAfterStart = null;
                lock (syncRoot)
                {
                    if (!roomDevices.TryGetValue(roomId, out var startDevices)) goto startCalibrationDone;
                    var startActor = startDevices.FirstOrDefault(entry => entry.DeviceId == deviceId);
                    if (startActor?.IsMaster is not true) goto startCalibrationDone;

                    roomsCalibrationLocked.Add(roomId);
                    roomAfterStart = BuildRoomResponse(roomId, startDevices, roomAudioStates, roomsCalibrationLocked);
                }

            startCalibrationDone:
                if (roomAfterStart is not null)
                {
                    await BroadcastRoomState(roomId, roomAfterStart, roomSockets, syncRoot);
                    await BroadcastSoundHomeState(soundHomeSockets, rooms, roomDevices, roomsCalibrationLocked, syncRoot);
                }
                continue;
            }

            if (messageType == "finish-calibration")
            {
                RoomDetailsResponse? roomAfterFinish = null;
                lock (syncRoot)
                {
                    if (!roomDevices.TryGetValue(roomId, out var finishDevices)) goto finishCalibrationDone;
                    var finishActor = finishDevices.FirstOrDefault(entry => entry.DeviceId == deviceId);
                    if (finishActor?.IsMaster is not true) goto finishCalibrationDone;

                    roomsCalibrationLocked.Remove(roomId);
                    roomAfterFinish = BuildRoomResponse(roomId, finishDevices, roomAudioStates, roomsCalibrationLocked);
                }

            finishCalibrationDone:
                if (roomAfterFinish is not null)
                {
                    await BroadcastRoomState(roomId, roomAfterFinish, roomSockets, syncRoot);
                    await BroadcastSoundHomeState(soundHomeSockets, rooms, roomDevices, roomsCalibrationLocked, syncRoot);
                }
                continue;
            }

            if (messageType == "sync-tone-start")
            {
                SyncToneStartMessage? toneStart;
                try
                {
                    toneStart = JsonSerializer.Deserialize<SyncToneStartMessage>(incomingText);
                }
                catch
                {
                    toneStart = null;
                }

                if (toneStart is null ||
                    string.IsNullOrWhiteSpace(toneStart.TargetDeviceId) ||
                    string.IsNullOrWhiteSpace(toneStart.SessionId))
                    continue;

                var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var canRelayTone = false;
                lock (syncRoot)
                {
                    if (roomDevices.TryGetValue(roomId, out var devices))
                    {
                        var actor = devices.FirstOrDefault(entry => entry.DeviceId == deviceId);
                        if (actor?.IsMaster is true &&
                            devices.Any(entry => entry.DeviceId == toneStart.TargetDeviceId))
                            canRelayTone = true;
                    }
                }

                if (!canRelayTone) continue;

                await BroadcastMessage(
                    roomId,
                    new
                    {
                        type = "sync-tone-start",
                        targetDeviceId = toneStart.TargetDeviceId,
                        sessionId = toneStart.SessionId,
                        iteration = toneStart.Iteration,
                        serverIssuedAtMs = issuedAt
                    },
                    roomSockets,
                    syncRoot
                );
                continue;
            }

            if (messageType == "sync-latency-report")
            {
                SyncLatencyReportMessage? latencyReport;
                try
                {
                    latencyReport = JsonSerializer.Deserialize<SyncLatencyReportMessage>(incomingText);
                }
                catch
                {
                    latencyReport = null;
                }

                if (latencyReport is null ||
                    string.IsNullOrWhiteSpace(latencyReport.DeviceId) ||
                    double.IsNaN(latencyReport.LagMs) ||
                    latencyReport.LagMs is < 0 or > 8000)
                    continue;

                RoomDetailsResponse? roomAfterLatency = null;
                lock (syncRoot)
                {
                    if (!roomDevices.TryGetValue(roomId, out var devices) ||
                        !roomAudioStates.TryGetValue(roomId, out var audioState))
                        goto latencyDone;

                    var actor = devices.FirstOrDefault(entry => entry.DeviceId == deviceId);
                    if (actor?.IsMaster is not true) goto latencyDone;

                    var target = devices.FirstOrDefault(entry => entry.DeviceId == latencyReport.DeviceId);
                    if (target is null || target.IsMaster) goto latencyDone;

                    var rev = audioState.Revision;
                    if (rev <= 0 || target.AudioReadyRevision < rev) goto latencyDone;

                    var updated = target with
                    {
                        PlaybackSyncLagMs = latencyReport.LagMs,
                        IsPlaybackSyncCalibrated = true,
                        PlaybackSyncRevision = rev,
                        LastSeenUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    ReplaceDevice(devices, updated);
                    roomAfterLatency = BuildRoomResponse(roomId, devices, roomAudioStates, roomsCalibrationLocked);
                }

            latencyDone:
                if (roomAfterLatency is not null)
                    await BroadcastRoomState(roomId, roomAfterLatency, roomSockets, syncRoot);
                continue;
            }

            if (messageType is not ("play-audio" or "pause-audio" or "stop-audio")) continue;

            RevisionAudioCommandMessage? payload;
            try
            {
                payload = JsonSerializer.Deserialize<RevisionAudioCommandMessage>(incomingText);
            }
            catch
            {
                payload = null;
            }
            if (payload is null || payload.Revision <= 0) continue;

            bool canPlay;
            bool canTransport;
            lock (syncRoot)
            {
                if (!roomDevices.TryGetValue(roomId, out var devices) || !roomAudioStates.TryGetValue(roomId, out var audioState))
                {
                    canPlay = false;
                    canTransport = false;
                }
                else
                {
                    var actor = devices.FirstOrDefault(entry => entry.DeviceId == deviceId);
                    var isMaster = actor?.IsMaster ?? false;
                    var hasAudio = !string.IsNullOrWhiteSpace(audioState.FileName) && audioState.Revision == payload.Revision;
                    var allAudioReady = devices.Count > 0 && devices.All(entry => entry.AudioReadyRevision >= payload.Revision);
                    var allPlaybackReady = allAudioReady && AreDevicesPlaybackSyncReady(devices, payload.Revision);
                    canPlay = isMaster && hasAudio && allPlaybackReady;
                    canTransport = isMaster && hasAudio;
                }
            }

            switch (messageType)
            {
                case "play-audio":
                    if (!canPlay) continue;
                    long serverStartMs;
                    double maxSyncLagMs;
                    lock (syncRoot)
                    {
                        maxSyncLagMs = roomDevices.TryGetValue(roomId, out var lagDevices)
                            ? ComputeMaxPlaybackSyncLagMs(lagDevices)
                            : 0;
                        serverStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 850L;
                    }

                    await BroadcastMessage(
                        roomId,
                        new { type = "play-audio", revision = payload.Revision, serverStartMs, maxSyncLagMs },
                        roomSockets,
                        syncRoot
                    );
                    break;
                case "pause-audio":
                    if (!canTransport) continue;
                    await BroadcastMessage(roomId, new { type = "pause-audio", revision = payload.Revision }, roomSockets, syncRoot);
                    break;
                case "stop-audio":
                    if (!canTransport) continue;
                    await BroadcastMessage(roomId, new { type = "stop-audio", revision = payload.Revision }, roomSockets, syncRoot);
                    break;
            }
        }
    }
    finally
    {
        RoomDetailsResponse? updatedRoom = null;
        lock (syncRoot)
        {
            if (roomSockets.TryGetValue(roomId, out var socketsByDevice))
            {
                socketsByDevice.Remove(deviceId);
                if (socketsByDevice.Count == 0) roomSockets.Remove(roomId);
            }

            if (roomDevices.TryGetValue(roomId, out var devices))
            {
                var leavingDevice = devices.FirstOrDefault(entry => entry.DeviceId == deviceId);
                var wasMaster = leavingDevice?.IsMaster ?? false;
                if (wasMaster && roomsCalibrationLocked.Contains(roomId)) roomsCalibrationLocked.Remove(roomId);

                devices.RemoveAll(entry => entry.DeviceId == deviceId);

                if (wasMaster && devices.Count > 0)
                {
                    var nextMaster = devices.OrderBy(entry => entry.FirstSeenUtc).First();
                    ReplaceDevice(devices, nextMaster with { IsMaster = true });
                }

                updatedRoom = BuildRoomResponse(roomId, devices, roomAudioStates, roomsCalibrationLocked);
            }
        }

        if (updatedRoom is not null)
            await BroadcastRoomState(roomId, updatedRoom, roomSockets, syncRoot);
        await BroadcastSoundHomeState(soundHomeSockets, rooms, roomDevices, roomsCalibrationLocked, syncRoot);

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", CancellationToken.None);
    }
});

app.Map("/ws/sound", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connectionId = Guid.NewGuid().ToString("N");

    lock (syncRoot)
        soundHomeSockets[connectionId] = socket;

    await BroadcastSoundHomeState(soundHomeSockets, rooms, roomDevices, roomsCalibrationLocked, syncRoot);

    var buffer = new byte[512];
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
            if (result.MessageType == WebSocketMessageType.Close) break;
        }
    }
    finally
    {
        lock (syncRoot)
            soundHomeSockets.Remove(connectionId);

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", CancellationToken.None);
    }
});

static RoomDetailsResponse BuildRoomResponse(
    string roomId,
    List<DeviceEntry> devices,
    Dictionary<string, RoomAudioState> roomAudioStates,
    HashSet<string> roomsCalibrationLocked
)
{
    roomAudioStates.TryGetValue(roomId, out var audioState);
    var audio = MapRoomAudio(audioState);
    var mappedDevices = devices.Select(device => MapDevice(device, audio.Revision)).ToList();
    var isCalibrationLocked = roomsCalibrationLocked.Contains(roomId);
    return new RoomDetailsResponse(roomId, mappedDevices, audio, isCalibrationLocked);
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

static bool IsAllowedAudioUpload(IFormFile file)
{
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    var isAllowedExtension = extension is ".mp3" or ".wav" or ".ogg" or ".aac" or ".m4a" or ".flac";
    if (!isAllowedExtension) return false;

    var contentType = file.ContentType.ToLowerInvariant();
    var isAllowedContentType = contentType is
        "audio/mpeg" or
        "audio/wav" or
        "audio/x-wav" or
        "audio/ogg" or
        "audio/aac" or
        "audio/mp4" or
        "audio/flac" or
        "application/octet-stream";
    if (!isAllowedContentType) return false;

    using var stream = file.OpenReadStream();
    return HasKnownAudioSignature(stream);
}

static bool HasKnownAudioSignature(Stream stream)
{
    var header = new byte[16];
    var read = stream.Read(header, 0, header.Length);
    if (read < 4) return false;

    var isWav = read >= 12 &&
        header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
        header[8] == (byte)'W' && header[9] == (byte)'A' && header[10] == (byte)'V' && header[11] == (byte)'E';
    if (isWav) return true;

    var isMp3Id3 = header[0] == (byte)'I' && header[1] == (byte)'D' && header[2] == (byte)'3';
    if (isMp3Id3) return true;

    var isMp3Frame = header[0] == 0xFF && (header[1] & 0xE0) == 0xE0;
    if (isMp3Frame) return true;

    var isOgg = header[0] == (byte)'O' && header[1] == (byte)'g' && header[2] == (byte)'g' && header[3] == (byte)'S';
    if (isOgg) return true;

    var isFlac = header[0] == (byte)'f' && header[1] == (byte)'L' && header[2] == (byte)'a' && header[3] == (byte)'C';
    if (isFlac) return true;

    var isAacAdts = header[0] == 0xFF && (header[1] & 0xF0) == 0xF0;
    if (isAacAdts) return true;

    var isM4a = read >= 12 &&
        header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p';
    return isM4a;
}

static async Task BroadcastRoomState(
    string roomId,
    RoomDetailsResponse room,
    Dictionary<string, Dictionary<string, WebSocket>> roomSockets,
    object syncRoot
)
{
    await BroadcastMessage(roomId, new { type = "room-state", room }, roomSockets, syncRoot);
}

static async Task BroadcastMessage(
    string roomId,
    object payload,
    Dictionary<string, Dictionary<string, WebSocket>> roomSockets,
    object syncRoot
)
{
    List<WebSocket> sockets;
    lock (syncRoot)
    {
        if (!roomSockets.TryGetValue(roomId, out var socketsByDevice) || socketsByDevice.Count == 0) return;
        sockets = socketsByDevice.Values.Where(socket => socket.State == WebSocketState.Open).ToList();
    }

    var message = JsonSerializer.Serialize(payload);
    var bytes = Encoding.UTF8.GetBytes(message);

    foreach (var socket in sockets)
    {
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch
        {
            // Socket might be closed concurrently.
        }
    }
}

static async Task BroadcastSoundHomeState(
    Dictionary<string, WebSocket> soundHomeSockets,
    HashSet<string> rooms,
    Dictionary<string, List<DeviceEntry>> roomDevices,
    HashSet<string> roomsCalibrationLocked,
    object syncRoot
)
{
    List<WebSocket> sockets;
    RoomsListResponse payload;
    lock (syncRoot)
    {
        sockets = soundHomeSockets.Values.Where(socket => socket.State == WebSocketState.Open).ToList();
        if (sockets.Count == 0) return;
        payload = new RoomsListResponse("rooms-state", BuildRoomsListResponse(rooms, roomDevices, roomsCalibrationLocked));
    }

    var message = JsonSerializer.Serialize(payload);
    var bytes = Encoding.UTF8.GetBytes(message);

    foreach (var socket in sockets)
    {
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch
        {
            // Socket could be closed by peer.
        }
    }
}

static List<RoomListItemResponse> BuildRoomsListResponse(
    HashSet<string> rooms,
    Dictionary<string, List<DeviceEntry>> roomDevices,
    HashSet<string> roomsCalibrationLocked
)
{
    return rooms
        .Where(id => !roomsCalibrationLocked.Contains(id))
        .OrderBy(id => id)
        .Select(id =>
        {
            var count = roomDevices.TryGetValue(id, out var devices) ? devices.Count : 0;
            return new RoomListItemResponse(id, count);
        })
        .ToList();
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

static bool AreDevicesPlaybackSyncReady(List<DeviceEntry> devices, long audioRevision)
{
    if (audioRevision <= 0) return false;
    if (devices.Count == 0) return false;

    foreach (var device in devices)
    {
        if (device.AudioReadyRevision < audioRevision) return false;
        if (device.IsMaster) continue;
        if (!device.IsPlaybackSyncCalibrated || device.PlaybackSyncRevision < audioRevision) return false;
    }

    return true;
}

static double ComputeMaxPlaybackSyncLagMs(List<DeviceEntry> devices)
{
    var max = 0.0;
    foreach (var device in devices)
    {
        if (device.IsMaster) continue;
        if (device.PlaybackSyncLagMs > max) max = device.PlaybackSyncLagMs;
    }

    return max;
}

static DeviceResponse MapDevice(DeviceEntry device, long audioRevision)
{
    var isAudioReady = audioRevision > 0 && device.AudioReadyRevision >= audioRevision;
    var isPlaybackSyncReady = audioRevision > 0 &&
        (device.IsMaster ||
            (device.IsPlaybackSyncCalibrated && device.PlaybackSyncRevision >= audioRevision));
    return new DeviceResponse(
        device.DeviceId,
        device.DisplayName,
        device.FirstSeenUtc,
        device.LastSeenUtc,
        device.DeviceInfo,
        device.IsMaster,
        device.IsOnline,
        isAudioReady,
        isPlaybackSyncReady,
        device.PlaybackSyncLagMs
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
record AudioReadyRequest([property: JsonPropertyName("revision")] long Revision);
record SyncToneStartMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("targetDeviceId")] string TargetDeviceId,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("iteration")] int Iteration
);
record SyncLatencyReportMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("lagMs")] double LagMs
);
record RevisionAudioCommandMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("revision")] long Revision
);
record RoomAudioState(string? FileName, long Revision, long? UpdatedAtUtc);
record RoomsListResponse(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("rooms")] List<RoomListItemResponse> Rooms
);
record RoomListItemResponse(
    [property: JsonPropertyName("roomId")] string RoomId,
    [property: JsonPropertyName("deviceCount")] int DeviceCount
);

record DeviceEntry(
    string DeviceId,
    string? DisplayName,
    long FirstSeenUtc,
    long LastSeenUtc,
    Dictionary<string, string> DeviceInfo,
    bool IsMaster,
    bool IsOnline,
    long AudioReadyRevision,
    bool IsPlaybackSyncCalibrated,
    double PlaybackSyncLagMs,
    long PlaybackSyncRevision
);

record RegisterDeviceResponse(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("room")] RoomDetailsResponse Room
);

record RoomDetailsResponse(
    [property: JsonPropertyName("roomId")] string RoomId,
    [property: JsonPropertyName("devices")] List<DeviceResponse> Devices,
    [property: JsonPropertyName("audio")] RoomAudioResponse Audio,
    [property: JsonPropertyName("isCalibrationLocked")] bool IsCalibrationLocked
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
    [property: JsonPropertyName("isMaster")] bool IsMaster,
    [property: JsonPropertyName("isOnline")] bool IsOnline,
    [property: JsonPropertyName("isAudioReady")] bool IsAudioReady,
    [property: JsonPropertyName("isPlaybackSyncReady")] bool IsPlaybackSyncReady,
    [property: JsonPropertyName("playbackSyncLagMs")] double PlaybackSyncLagMs
);
