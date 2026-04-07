import { requestJson } from "@/utils/requestManager"

export type ServerTimeResponse = {
  unixSeconds: number
}

export async function getServerTimeSeconds(): Promise<number> {
  const payload = await requestJson<ServerTimeResponse>("/api/time", {
    method: "GET"
  })
  return payload.unixSeconds
}
