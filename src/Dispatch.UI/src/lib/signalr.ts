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
