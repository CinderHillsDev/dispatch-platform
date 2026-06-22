export interface SpoolCounts { incoming: number; processing: number; failed: number; }

export type IntakeLevel = "Normal" | "Throttled" | "Suspended";

export interface Stats {
  received: number; delivered: number; failed: number; retried: number; denied: number;
  spool: SpoolCounts;
  intake: IntakeLevel;
}

export interface MessageRow {
  id: number;
  loggedAt: string;
  event: string;
  status: string;
  spoolId: string;
  fromAddress: string;
  toDomain: string;
  toAddresses: string[];
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

export interface MessageDetail {
  id: number;
  loggedAt: string;
  event: string;
  status: string;
  spoolId: string;
  retryAttempt: number;
  fromAddress: string;
  fromDomain: string;
  toAddresses: string[];
  toDomain: string;
  subject: string | null;
  sizeBytes: number;
  relayName: string | null;
  routingRuleName: string | null;
  routingMatched: boolean;
  provider: string | null;
  providerMessageId: string | null;
  providerResponse: string | null;
  durationMs: number | null;
  error: string | null;
  ingestSource: string;
  sourceIp: string | null;
  apiKeyName: string | null;
  tags: string[];
  xMailer: string | null;
  attachmentCount: number;
  history: MessageAttempt[];
}

export interface MessageAttempt {
  loggedAt: string;
  event: string;
  status: string;
  retryAttempt: number;
  provider: string | null;
  durationMs: number | null;
  error: string | null;
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

export interface TestRunStart { runId: string; status: string; }
export interface TestRunLine { ts: string; level: string; message: string; }
export interface TestRun { runId: string; status: string; provider: string; durationMs: number; lines: TestRunLine[]; }

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
export interface InboxAttachment { index: number; name: string; contentType: string; sizeBytes: number; }
export interface InboxMessage extends InboxItem { cc: string; text: string | null; html: string | null; attachments: InboxAttachment[]; }

export interface FailedItem {
  id: string; from: string; to: string[]; subject: string;
  retryCount: number; lastError: string | null; ingestSource: string; failedAt: string; sizeBytes: number;
}
export interface FailedMessage {
  id: string; from: string; to: string; subject: string;
  text: string | null; html: string | null; retryCount: number; lastError: string | null;
}

export interface SmtpCredential { id: number; username: string; createdAt: string; lastUsedAt: string | null; }

export interface ApiKeyItem {
  id: number;
  keyId: string;
  name: string;
  createdAt: string;
  lastUsedAt: string | null;
  messageCount: number;
  revoked: boolean;
  rateLimitPerMinute: number;
}
export interface ApiKeyCreated {
  id: number;
  keyId: string;
  name: string;
  rateLimitPerMinute: number;
  key: string;   // plaintext, shown once
}

export interface RelayStat {
  id: number; name: string; provider: string; isDefault: boolean; enabled: boolean;
  received: number; delivered: number; failed: number; inFlight: number;
}

export interface AppSettings {
  logging: { delivered: boolean; retrying: boolean; denied: boolean };
  retry: { maxRetries: number; retryDelaysSeconds: number[] };
  retention: {
    logDeliveredRetentionDays: number;
    logFailedRetentionDays: number;
    spoolFailedRetentionDays: number;
    capturedRetentionDays: number;
    auditRetentionDays: number;
    auditSecurityRetentionDays: number;
    sizeTriggerGb: number;
    sizeTargetGb: number;
  };
}

export interface SystemConfig {
  listener: {
    ports: number[]; serverName: string; allowedCidrs: string[];
    maxMessageBytes: number; requireAuth: boolean;
    tlsEnabled: boolean; appliesOnRestart: string[];
  };
  // Shared TLS certificate (SMTP STARTTLS + HTTPS API). source: "generated" | "uploaded" | "".
  tls: { source: string };
  api: {
    port: number; httpEnabled: boolean; tlsEnabled: boolean; tlsPort: number;
    allowedCidrs: string[]; maxMessageBytes: number; rateLimitPerKey: number; appliesOnRestart: string[];
  };
  webui: { port: number; requireHttps: boolean; appliesOnRestart: string[] };
  spool: { directory: string; workerCount: number; appliesOnRestart: string[] };
}

export interface PurgeRun {
  ranAtUtc: string;
  manual: boolean;
  spoolFilesDeleted: number;
  logRowsDeleted: number;
  databaseSizeBytes: number;
}

export interface SystemInfo {
  version: string;
  uptimeSeconds: number;
  startedAtUtc: string;
  os: string;
  framework: string;
  logDirectory: string;
}

async function getJson<T>(url: string): Promise<T> {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.json() as Promise<T>;
}

async function sendJson<T>(url: string, method: string, body: unknown): Promise<T> {
  const res = await fetch(url, {
    method,
    // X-Dispatch-Request is the CSRF guard: the server requires it on state-changing calls, and a custom
    // header can't be set cross-origin without a CORS preflight the server never grants.
    headers: { "Content-Type": "application/json", "X-Dispatch-Request": "1" },
    body: JSON.stringify(body),
  });
  const text = await res.text();
  const data = text ? JSON.parse(text) : {};
  if (!res.ok) throw new Error(data?.error ?? `${res.status} ${res.statusText}`);
  return data as T;
}

export interface AuditEntry {
  id: number; loggedAt: string; kind: string; category: string; event: string;
  severity: string; actor: string | null; sourceIp: string | null; detail: string | null;
}
export interface AuditPage { rows: AuditEntry[]; nextCursor: { at: string; id: number } | null; }

export interface ReportSummary { received: number; delivered: number; failed: number; retried: number; denied: number; }
export interface ReportDaily { date: string; received: number; delivered: number; failed: number; retried: number; denied: number; }
export interface ReportRelay { relayId: number; relayName: string; received: number; delivered: number; failed: number; retried: number; denied: number; }
export interface ReportData { from: string; to: string; summary: ReportSummary; daily: ReportDaily[]; relays: ReportRelay[]; }

export const api = {
  stats: () => getJson<Stats>("/api/stats"),
  throughput: () => getJson<number[]>("/api/stats/throughput"),
  relayStats: () => getJson<RelayStat[]>("/api/stats/relays"),
  reports: (from: string, to: string) => getJson<ReportData>(`/api/reports?from=${from}&to=${to}`),
  audit: (params: URLSearchParams) => getJson<AuditPage>(`/api/audit?${params}`),
  setPassword: (password: string) => sendJson<{ ok: boolean }>("/api/auth/password", "POST", { password }),
  messages: (params: URLSearchParams) => getJson<MessagePage>(`/api/messages?${params}`),
  message: (id: number) => getJson<MessageDetail>(`/api/messages/${id}`),
  messageIdBySpool: (spoolId: string) => getJson<{ id: number }>(`/api/messages/by-spool/${encodeURIComponent(spoolId)}`),

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

  config: {
    testProvider: (provider: string, settings: Record<string, string>, testRecipient: string) =>
      sendJson<TestRunStart>("/api/config/test-provider", "POST", { provider, settings, testRecipient }),
    testProviderRun: (runId: string) => getJson<TestRun>(`/api/config/test-provider/${encodeURIComponent(runId)}`),
  },

  rules: {
    list: () => getJson<RuleItem[]>("/api/routing/rules"),
    create: (body: { name: string; recipientPattern: string | null; senderPattern: string | null; relayId: number }) =>
      sendJson<{ id: number }>("/api/routing/rules", "POST", body),
    update: (id: number, body: { name: string; recipientPattern: string | null; senderPattern: string | null; relayId: number; enabled?: boolean }) =>
      sendJson<{ ok: boolean }>(`/api/routing/rules/${id}`, "PUT", body),
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

  keys: {
    list: () => getJson<ApiKeyItem[]>("/api/keys"),
    create: (name: string, rateLimitPerMinute: number | null) =>
      sendJson<ApiKeyCreated>("/api/keys", "POST", { name, rateLimitPerMinute }),
    revoke: (id: number) => sendJson<Record<string, never>>(`/api/keys/${id}`, "DELETE", {}),
  },

  settings: {
    get: () => getJson<AppSettings>("/api/settings"),
    config: () => getJson<SystemConfig>("/api/config"),
    putListener: (d: Partial<{ ports: number[]; serverName: string; allowedCidrs: string[]; maxMessageBytes: number; requireAuth: boolean; tlsCertPath: string; tlsCertPassword: string }>) =>
      sendJson<{ ok: boolean }>("/api/config/listener", "PUT", d),
    putApi: (d: Partial<{ port: number; httpEnabled: boolean; tlsEnabled: boolean; tlsPort: number; allowedCidrs: string[]; maxMessageBytes: number; rateLimitPerKey: number }>) =>
      sendJson<{ ok: boolean }>("/api/config/api", "PUT", d),
    putWebui: (d: Partial<{ port: number; allowedCidrs: string[]; requireHttps: boolean; sessionTimeoutMinutes: number }>) =>
      sendJson<{ ok: boolean }>("/api/config/webui", "PUT", d),
    putSpool: (d: Partial<{ directory: string; workerCount: number }>) =>
      sendJson<{ ok: boolean }>("/api/config/spool", "PUT", d),
    saveLogging: (logging: AppSettings["logging"]) => sendJson<{ ok: boolean }>("/api/settings", "PUT", { logging }),
    saveRetry: (retry: AppSettings["retry"]) => sendJson<{ ok: boolean }>("/api/settings", "PUT", { retry }),
    saveRetention: (retention: AppSettings["retention"]) => sendJson<{ ok: boolean }>("/api/settings", "PUT", { retention }),
    generateTlsCert: () => sendJson<{ ok: boolean; source: string }>("/api/config/tls-cert/generate", "POST", {}),
    removeTlsCert: () => sendJson<{ ok: boolean }>("/api/config/tls-cert", "DELETE", {}),
    uploadTlsCert: async (cert: File, key: File) => {
      const fd = new FormData();
      fd.append("cert", cert);
      fd.append("key", key);
      const res = await fetch("/api/config/tls-cert/upload", { method: "POST", headers: { "X-Dispatch-Request": "1" }, body: fd });
      const text = await res.text();
      const data = text ? JSON.parse(text) : {};
      if (!res.ok) throw new Error(data?.error ?? `${res.status} ${res.statusText}`);
      return data as { ok: boolean; source: string };
    },
  },

  purge: {
    run: () => sendJson<PurgeRun>("/api/purge/run", "POST", {}),
    history: () => getJson<PurgeRun[]>("/api/purge/history"),
  },

  system: {
    about: () => getJson<SystemInfo>("/api/system"),
    restart: () => sendJson<{ restarting: boolean }>("/api/service/restart", "POST", {}),
    logDownloadUrl: "/api/logs/download",
  },
};
