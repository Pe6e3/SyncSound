import { requestJson } from "@/utils/requestManager"

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
  isPlaybackSyncReady: boolean
  playbackSyncLagMs: number
}

export type RoomDetailsResponse = {
  roomId: string
  devices: DeviceResponse[]
  audio: RoomAudioResponse
  isCalibrationLocked: boolean
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

export type AudioByteProgress = (loaded: number, total: number | null) => void

function postRoomAudioWithProgress(url: string, actorDeviceId: string, file: File, onProgress: AudioByteProgress): Promise<RoomDetailsResponse> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    xhr.open("POST", url)
    xhr.responseType = "json"

    const totalBytes = file.size > 0 ? file.size : null
    onProgress(0, totalBytes)

    xhr.upload.onprogress = event => {
      if (event.lengthComputable && event.total > 0) onProgress(event.loaded, event.total)
      else if (totalBytes !== null) onProgress(event.loaded, totalBytes)
      else onProgress(event.loaded, null)
    }

    xhr.onload = () => {
      if (xhr.status < 200 || xhr.status >= 300) {
        reject(new Error(`Request failed with status ${xhr.status}`))
        return
      }
      const raw = xhr.response
      if (raw && typeof raw === "object") {
        onProgress(totalBytes ?? 0, totalBytes)
        resolve(raw as RoomDetailsResponse)
        return
      }
      try {
        const parsed = JSON.parse(xhr.responseText as string) as RoomDetailsResponse
        onProgress(totalBytes ?? 0, totalBytes)
        resolve(parsed)
      } catch {
        reject(new Error("Некорректный ответ сервера при загрузке аудио"))
      }
    }

    xhr.onerror = () => reject(new Error("Сеть: не удалось загрузить файл"))
    xhr.onabort = () => reject(new Error("Загрузка прервана"))

    const formData = new FormData()
    formData.append("actorDeviceId", actorDeviceId)
    formData.append("file", file)
    xhr.send(formData)
  })
}

export async function uploadRoomAudio(
  roomId: string,
  actorDeviceId: string,
  file: File,
  onProgress?: AudioByteProgress
): Promise<RoomDetailsResponse> {
  const url = `/api/rooms/${roomId}/audio`

  if (onProgress && typeof XMLHttpRequest !== "undefined") {
    return postRoomAudioWithProgress(url, actorDeviceId, file, onProgress)
  }

  const formData = new FormData()
  formData.append("actorDeviceId", actorDeviceId)
  formData.append("file", file)

  return await requestJson<RoomDetailsResponse>(url, {
    method: "POST",
    body: formData
  })
}

/** @param onProgress — (0–100) при известном Content-Length; иначе вызывается с `null` (неизвестный прогресс). */
export async function downloadRoomAudio(roomId: string, onPercent?: (percent: number | null) => void): Promise<Blob> {
  const url = `/api/rooms/${roomId}/audio`
  const response = await fetch(url, { method: "GET" })
  if (!response.ok) throw new Error(`Request failed with status ${response.status}`)

  if (!onPercent) return await response.blob()

  const headerLen = response.headers.get("Content-Length")
  const total = headerLen ? parseInt(headerLen, 10) : NaN
  const hasTotal = Number.isFinite(total) && total > 0
  const body = response.body

  onPercent(hasTotal ? 0 : null)

  if (!body) {
    const blob = await response.blob()
    onPercent(100)
    return blob
  }

  const reader = body.getReader()
  const chunks: Uint8Array[] = []
  let loaded = 0

  while (true) {
    const chunk = await reader.read()
    if (chunk.done) break
    const value = chunk.value
    if (value && value.length) {
      chunks.push(value)
      loaded += value.length
      if (hasTotal) onPercent(Math.min(100, Math.round((loaded / total) * 100)))
      else onPercent(null)
    }
  }

  onPercent(100)
  return new Blob(chunks as BlobPart[])
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
