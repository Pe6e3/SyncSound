export type CreateRoomResponse = {
  roomId: string
}

export type RoomResponse = {
  roomId: string
}

export type DeviceResponse = {
  deviceId: string
  displayName: string | null
  firstSeenUtc: number
  lastSeenUtc: number
  deviceInfo: Record<string, string>
  isMaster: boolean
}

export type RoomDetailsResponse = {
  roomId: string
  devices: DeviceResponse[]
}

export type RegisterDevicePayload = {
  deviceId?: string
  displayName?: string
  deviceInfo: Record<string, string>
}

export type RegisterDeviceResponse = {
  deviceId: string
  room: RoomDetailsResponse
}

export async function getRooms(): Promise<string[]> {
  const response = await fetch("/api/rooms", {
    method: "GET"
  })

  if (!response.ok) throw new Error(`Request failed with status ${response.status}`)
  const payload = (await response.json()) as RoomResponse[]
  return payload.map(item => item.roomId)
}

export async function createRoom(): Promise<string> {
  const response = await fetch("/api/rooms", {
    method: "POST"
  })

  if (!response.ok) throw new Error(`Request failed with status ${response.status}`)
  const payload = (await response.json()) as CreateRoomResponse
  return payload.roomId
}

export async function getRoom(roomId: string): Promise<RoomDetailsResponse> {
  const response = await fetch(`/api/rooms/${roomId}`, {
    method: "GET"
  })

  if (!response.ok) throw new Error(`Request failed with status ${response.status}`)
  return (await response.json()) as RoomDetailsResponse
}

export async function registerDevice(roomId: string, payload: RegisterDevicePayload): Promise<RegisterDeviceResponse> {
  const response = await fetch(`/api/rooms/${roomId}/devices/register`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify(payload)
  })

  if (!response.ok) throw new Error(`Request failed with status ${response.status}`)
  return (await response.json()) as RegisterDeviceResponse
}

export async function updateDeviceName(roomId: string, deviceId: string, displayName: string): Promise<RoomDetailsResponse> {
  const response = await fetch(`/api/rooms/${roomId}/devices/${deviceId}/name`, {
    method: "PATCH",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify({ displayName })
  })

  if (!response.ok) throw new Error(`Request failed with status ${response.status}`)
  return (await response.json()) as RoomDetailsResponse
}

export async function transferMaster(roomId: string, targetDeviceId: string, actorDeviceId: string): Promise<RoomDetailsResponse> {
  const response = await fetch(`/api/rooms/${roomId}/devices/${targetDeviceId}/master`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify({ actorDeviceId })
  })

  if (!response.ok) throw new Error(`Request failed with status ${response.status}`)
  return (await response.json()) as RoomDetailsResponse
}
