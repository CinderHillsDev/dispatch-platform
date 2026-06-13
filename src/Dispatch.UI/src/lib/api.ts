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
export interface RelayListItem { id: number; name: string; provider: string; isDefault: boolean; enabled: boolean; maxConcurrency: number; }
export interface RelayDetail extends RelayListItem { providers: string[]; fields: RelayField[]; }
export interface TestResult { ok: boolean; provider?: string; providerMessageId?: string; detail?: string; error?: string; }

export interface RuleItem {
  id: number; priority: number; name: string;
  recipientPattern: string | null; senderPattern: string | null;
  relayId: number; relayName: string; enabled: boolean;
}
export interface SimulateResult {
  matched: boolean; matchedRuleId: number | null; matchedRuleName: string | null;
  relayId: number; relayName: string; provider: string;
}

export interface InboxItem { id: string; from: string; to: string; subject: string; date: string; sizeBytes: number; }
export interface InboxMessage extends InboxItem { cc: string; text: string | null; html: string | null; }

export interface FailedItem {
  id: string; from: string; to: string[]; subject: string;
  retryCount: number; lastError: string | null; ingestSource: string; failedAt: string; sizeBytes: number;
}
export interface FailedMessage {
  id: string; from: string; to: string; subject: string;
  text: string | null; html: string | null; retryCount: number; lastError: string | null;
}

export interface SmtpCredential { id: number; username: string; createdAt: string; lastUsedAt: string | null; }

export interface RelayStat {
  id: number; name: string; provider: string; isDefault: boolean; enabled: boolean;
  received: number; delivered: number; failed: number; inFlight: number;
}

export interface AppSettings { logging: { delivered: boolean; retrying: boolean; denied: boolean }; }

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
  relayStats: () => getJson<RelayStat[]>("/api/stats/relays"),
  messages: (params: URLSearchParams) => getJson<MessagePage>(`/api/messages?${params}`),

  relays: {
    list: () => getJson<RelayListItem[]>("/api/relays"),
    get: (id: number) => getJson<RelayDetail>(`/api/relays/${id}`),
    create: (name: string, provider: string) =>
      sendJson<{ id: number }>("/api/relays", "POST", { name, provider }),
    update: (id: number, body: { name: string; provider: string; enabled: boolean; maxConcurrency: number; settings: Record<string, string> }) =>
      sendJson<{ ok: boolean }>(`/api/relays/${id}`, "PUT", body),
    setDefault: (id: number) => sendJson<{ ok: boolean }>(`/api/relays/${id}/set-default`, "POST", {}),
    remove: (id: number) => sendJson<{ ok: boolean }>(`/api/relays/${id}`, "DELETE", {}),
    test: (id: number, to: string) => sendJson<TestResult>(`/api/relays/${id}/test`, "POST", { to }),
  },

  rules: {
    list: () => getJson<RuleItem[]>("/api/routing/rules"),
    create: (body: { name: string; recipientPattern: string | null; senderPattern: string | null; relayId: number }) =>
      sendJson<{ id: number }>("/api/routing/rules", "POST", body),
    remove: (id: number) => sendJson<{ ok: boolean }>(`/api/routing/rules/${id}`, "DELETE", {}),
    reorder: (ids: number[]) => sendJson<{ ok: boolean }>("/api/routing/rules/reorder", "PUT", { ids }),
    simulate: (from: string, to: string) => sendJson<SimulateResult>("/api/routing/simulate", "POST", { from, to }),
  },

  inbox: {
    list: () => getJson<InboxItem[]>("/api/local/messages"),
    get: (id: string) => getJson<InboxMessage>(`/api/local/messages/${encodeURIComponent(id)}`),
    remove: (id: string) => sendJson<{ ok: boolean }>(`/api/local/messages/${encodeURIComponent(id)}`, "DELETE", {}),
    clear: () => sendJson<{ ok: boolean }>("/api/local/messages", "DELETE", {}),
  },

  failed: {
    list: () => getJson<FailedItem[]>("/api/failed"),
    get: (id: string) => getJson<FailedMessage>(`/api/failed/${encodeURIComponent(id)}`),
    retry: (id: string) => sendJson<{ ok: boolean }>(`/api/failed/${encodeURIComponent(id)}/retry`, "POST", {}),
    remove: (id: string) => sendJson<{ ok: boolean }>(`/api/failed/${encodeURIComponent(id)}`, "DELETE", {}),
  },

  smtpCreds: {
    list: () => getJson<SmtpCredential[]>("/api/smtp-credentials"),
    add: (username: string, password: string) => sendJson<{ ok: boolean }>("/api/smtp-credentials", "POST", { username, password }),
    remove: (username: string) => sendJson<{ ok: boolean }>(`/api/smtp-credentials/${encodeURIComponent(username)}`, "DELETE", {}),
  },

  settings: {
    get: () => getJson<AppSettings>("/api/settings"),
    saveLogging: (logging: AppSettings["logging"]) => sendJson<{ ok: boolean }>("/api/settings", "PUT", { logging }),
  },
};
