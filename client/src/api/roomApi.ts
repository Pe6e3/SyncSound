import { requestBlob, requestJson } from "@/utils/requestManager"

export type CreateRoomResponse = {
  roomId: string
}

export type RoomResponse = {
  roomId: string
  deviceCount: number
}

export type DeviceResponse = {
  deviceId: string
  displayName: string | null
  firstSeenUtc: number
  lastSeenUtc: number
  deviceInfo: Record<string, string>
  isMaster: boolean
  isOnline: boolean
  isAudioReady: boolean
}

export type RoomDetailsResponse = {
  roomId: string
  devices: DeviceResponse[]
  audio: RoomAudioResponse
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

export type RoomAudioResponse = {
  hasAudio: boolean
  fileName: string | null
  revision: number
  updatedAtUtc: number | null
}

export type RoomListItem = {
  roomId: string
  deviceCount: number
}

export async function getRooms(): Promise<RoomListItem[]> {
  return await requestJson<RoomResponse[]>("/api/rooms", {
    method: "GET"
  })
}

export async function createRoom(): Promise<string> {
  const payload = await requestJson<CreateRoomResponse>("/api/rooms", {
    method: "POST"
  })
  return payload.roomId
}

export async function getRoom(roomId: string): Promise<RoomDetailsResponse> {
  return await requestJson<RoomDetailsResponse>(`/api/rooms/${roomId}`, {
    method: "GET"
  })
}

export async function registerDevice(roomId: string, payload: RegisterDevicePayload): Promise<RegisterDeviceResponse> {
  return await requestJson<RegisterDeviceResponse>(`/api/rooms/${roomId}/devices/register`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify(payload)
  })
}

export async function updateDeviceName(roomId: string, deviceId: string, displayName: string): Promise<RoomDetailsResponse> {
  return await requestJson<RoomDetailsResponse>(`/api/rooms/${roomId}/devices/${deviceId}/name`, {
    method: "PATCH",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify({ displayName })
  })
}

export async function transferMaster(roomId: string, targetDeviceId: string, actorDeviceId: string): Promise<RoomDetailsResponse> {
  return await requestJson<RoomDetailsResponse>(`/api/rooms/${roomId}/devices/${targetDeviceId}/master`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify({ actorDeviceId })
  })
}

export async function uploadRoomAudio(roomId: string, actorDeviceId: string, file: File): Promise<RoomDetailsResponse> {
  const formData = new FormData()
  formData.append("actorDeviceId", actorDeviceId)
  formData.append("file", file)

  return await requestJson<RoomDetailsResponse>(`/api/rooms/${roomId}/audio`, {
    method: "POST",
    body: formData
  })
}

export async function downloadRoomAudio(roomId: string): Promise<Blob> {
  return await requestBlob(`/api/rooms/${roomId}/audio`, {
    method: "GET"
  })
}

export async function reportAudioReady(roomId: string, deviceId: string, revision: number): Promise<RoomDetailsResponse> {
  return await requestJson<RoomDetailsResponse>(`/api/rooms/${roomId}/devices/${deviceId}/audio-ready`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify({ revision })
  })
}
