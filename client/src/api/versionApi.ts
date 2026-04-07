export type VersionResponse = {
  version: string
}

export async function getVersion(): Promise<string> {
  const response = await fetch("/api/version", {
    method: "GET"
  })

  if (!response.ok) throw new Error(`Request failed with status ${response.status}`)
  const payload = (await response.json()) as VersionResponse
  return payload.version
}
