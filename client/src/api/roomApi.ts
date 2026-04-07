export type CreateRoomResponse = {
  roomId: string
}

export type RoomResponse = {
  roomId: string
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

export async function getRoom(roomId: string): Promise<string> {
  const response = await fetch(`/api/rooms/${roomId}`, {
    method: "GET"
  })

  if (!response.ok) throw new Error(`Request failed with status ${response.status}`)
  const payload = (await response.json()) as RoomResponse
  return payload.roomId
}
