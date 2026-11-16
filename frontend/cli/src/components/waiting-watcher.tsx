import { useEffect, useRef } from "react";
import { HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { useLocation } from "react-router";
import { getAuth } from "../lib/auth";
import { baseUrl, cerberus, type Job } from "../lib/cerberus/api";
import { createWaitingWatcher } from "../lib/cerberus/waiting";
import { useToast } from "../providers/toast";

/** Live job updates for the signed-in operator, over the same hub a run's screen uses. */
export function connectToJobs(onJob: (job: Job) => void) {
  const connection = new HubConnectionBuilder()
    .withUrl(`${baseUrl()}/hub/jobs`, { accessTokenFactory: () => getAuth()?.token ?? "" })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.None)
    .build();
  connection.on("jobUpdated", onJob);

  return {
    ensure: async () => {
      if (connection.state !== HubConnectionState.Disconnected) return;
      try {
        await connection.start();
      } catch {
        // No push channel right now; the periodic re-read still catches everything, a little later.
      }
    },
    stop: () => {
      void connection.stop();
    },
  };
}

/**
 * Renders nothing. While mounted, tells the operator — wherever they are in
 * the app — when one of their runs starts waiting for them or ends.
 */
export function WaitingWatcher() {
  const { show } = useToast();
  const location = useLocation();
  const path = useRef(location.pathname);
  path.current = location.pathname;

  useEffect(() => {
    const watcher = createWaitingWatcher({
      signedIn: () => getAuth() !== null,
      listJobs: () => cerberus.jobs(50),
      connect: connectToJobs,
      notify: (notice) => show({ variant: notice.variant, message: notice.message }),
      isViewing: (jobId) => path.current === `/runs/${jobId}`,
    });
    return watcher.stop;
  }, [show]);

  return null;
}
