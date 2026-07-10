import { HubConnectionBuilder, HubConnection, LogLevel } from "@microsoft/signalr";
import type { RelayEvent } from "./api";

/// Builds a SignalR connection to the relay log hub. The caller wires `recent` (replay on connect)
/// and `relayEvent` (live) handlers, then starts it.
export function createLogConnection(
  onRecent: (events: RelayEvent[]) => void,
  onEvent: (event: RelayEvent) => void,
): HubConnection {
  const conn = new HubConnectionBuilder()
    .withUrl("/hub/logs")
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();

  conn.on("recent", onRecent);
  conn.on("relayEvent", onEvent);
  return conn;
}

export interface TestProviderLogLine { runId: string; ts: string; level: string; message: string; }

/// Builds a SignalR connection to the provider-test hub (spec §11). The caller joins the run's group
/// (`conn.invoke("Join", runId)`) once started, and wires the `TestProviderLogLine` handler.
export function createTestProviderConnection(
  onLine: (line: TestProviderLogLine) => void,
): HubConnection {
  const conn = new HubConnectionBuilder()
    .withUrl("/hub/test-provider")
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();

  conn.on("TestProviderLogLine", onLine);
  return conn;
}
