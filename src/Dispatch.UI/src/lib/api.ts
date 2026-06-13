export interface SpoolCounts { incoming: number; processing: number; failed: number; }

export interface Stats {
  received: number; delivered: number; failed: number; retried: number; denied: number;
  spool: SpoolCounts;
}

export interface MessageRow {
  id: number;
  loggedAt: string;
  event: string;
  status: string;
  spoolId: string;
  fromAddress: string;
  toDomain: string;
  subject: string | null;
  relayName: string | null;
  provider: string | null;
  durationMs: number | null;
  sizeBytes: number;
  ingestSource: string;
  retryAttempt: number;
  error: string | null;
}

export interface MessagePage {
  rows: MessageRow[];
  nextCursor: { at: string; id: number } | null;
}

export interface RelayEvent {
  loggedAt: string;
  event: string;
  status: string;
  spoolId: string;
  fromAddress: string;
  toDomain: string;
  subject: string | null;
  relayName: string | null;
  provider: string | null;
  durationMs: number | null;
  ingestSource: string;
}

export interface RelayField { name: string; secret: boolean; required: boolean; hasValue: boolean; value: string; }
export interface RelayConfig {
  relayId: number;
  name: string;
  provider: string;
  providers: string[];
  fields: RelayField[];
}
export interface TestResult { ok: boolean; provider?: string; providerMessageId?: string; detail?: string; error?: string; }

async function getJson<T>(url: string): Promise<T> {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.json() as Promise<T>;
}

async function sendJson<T>(url: string, method: string, body: unknown): Promise<T> {
  const res = await fetch(url, {
    method,
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  const text = await res.text();
  const data = text ? JSON.parse(text) : {};
  if (!res.ok) throw new Error(data?.error ?? `${res.status} ${res.statusText}`);
  return data as T;
}

export const api = {
  stats: () => getJson<Stats>("/api/stats"),
  throughput: () => getJson<number[]>("/api/stats/throughput"),
  messages: (params: URLSearchParams) => getJson<MessagePage>(`/api/messages?${params}`),
  relay: () => getJson<RelayConfig>("/api/relay"),
  saveRelay: (provider: string, settings: Record<string, string>) =>
    sendJson<{ ok: boolean }>("/api/relay", "PUT", { provider, settings }),
  testRelay: (to: string, from?: string) =>
    sendJson<TestResult>("/api/relay/test", "POST", { to, from }),
};
