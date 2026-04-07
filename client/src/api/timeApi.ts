export type ServerTimeResponse = {
  unixSeconds: number
}

export async function getServerTimeSeconds(): Promise<number> {
  const response = await fetch("/api/time", {
    method: "GET"
  })

  if (!response.ok) throw new Error(`Request failed with status ${response.status}`)

  const payload = (await response.json()) as ServerTimeResponse
  return payload.unixSeconds
}
