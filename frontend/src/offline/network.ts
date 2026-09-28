/** Bound a dead Tailscale connection even when the browser reports online. */
export async function fetchWithTimeout(input: RequestInfo | URL, init?: RequestInit, fetcher: typeof fetch = fetch): Promise<Response> {
  const signal = AbortSignal.timeout(10_000)
  return fetcher(input, { ...init, signal: init?.signal ? AbortSignal.any([init.signal, signal]) : signal })
}
