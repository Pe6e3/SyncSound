import { requestJson } from "@/utils/requestManager"

export type VersionResponse = {
  version: string
}

export async function getVersion(): Promise<string> {
  const payload = await requestJson<VersionResponse>("/api/version", {
    method: "GET"
  })
  return payload.version
}
