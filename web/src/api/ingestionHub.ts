import { useEffect, useRef, useState } from "react";
import { HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { getAccessToken } from "./client";

export interface DocumentStatusEvent {
  tenantId: string;
  workspaceId: string;
  documentId: string;
  status: "Pending" | "Processing" | "Ready" | "Failed";
  chunkCount: number;
  failureReason: string | null;
}

/**
 * Subscribes to live ingestion progress for one workspace. The server only accepts the subscription if the
 * user may read the workspace. Reconnects automatically (and re-subscribes). Returns whether the live
 * channel is currently connected, so callers can fall back to polling when it is not.
 */
export function useIngestionUpdates(workspaceId: string, onUpdate: (e: DocumentStatusEvent) => void): boolean {
  const [connected, setConnected] = useState(false);
  const handler = useRef(onUpdate);
  handler.current = onUpdate;

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl("/hubs/ingestion", { accessTokenFactory: async () => (await getAccessToken()) ?? "" })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    const subscribe = async () => {
      try {
        await connection.invoke("Subscribe", workspaceId);
        setConnected(true);
      } catch {
        setConnected(false); // not allowed or transient; the polling fallback keeps the UI correct
      }
    };

    connection.on("documentStatus", (e: DocumentStatusEvent) => handler.current(e));
    connection.onreconnecting(() => setConnected(false));
    connection.onreconnected(() => void subscribe());
    connection.onclose(() => setConnected(false));

    connection
      .start()
      .then(subscribe)
      .catch(() => setConnected(false));

    return () => {
      if (connection.state !== HubConnectionState.Disconnected) void connection.stop();
    };
  }, [workspaceId]);

  return connected;
}
