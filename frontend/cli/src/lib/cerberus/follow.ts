import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from "@microsoft/signalr";
import { getAuth } from "../auth";
import { baseUrl, cerberus, type RunEvent } from "./api";
import { applyEvents, emptyRun, isFinished, type RunView } from "./run-state";

// Follows one run live. Events arrive over SignalR as they happen; whatever
// was missed (before connecting, during a reconnect, or if the push channel
// is unavailable) is read from the stored stream, so the view is always the
// complete run in order.

const SAFETY_POLL_MS = 4000;

export type Follower = { stop: () => Promise<void> };

export function followRun(jobId: string, onChange: (view: RunView) => void, onError?: (error: Error) => void): Follower {
  let view = emptyRun();
  let stopped = false;
  let catchingUp: Promise<void> | null = null;
  let timer: ReturnType<typeof setInterval> | null = null;
  let connection: HubConnection | null = null;

  const apply = (events: RunEvent[]) => {
    const before = view.lastSequence;
    const result = applyEvents(view, events);
    view = result.view;
    if (view.lastSequence !== before) onChange(view);
    return result.gap;
  };

  /** Reads everything after what we have. Serialized, so two triggers never interleave. */
  const catchUp = (): Promise<void> => {
    catchingUp ??= (async () => {
      try {
        let more = true;
        while (more && !stopped) {
          const page = await cerberus.events(jobId, view.lastSequence);
          apply(page.events);
          more = page.more;
        }
      } catch (error) {
        if (!stopped) onError?.(error instanceof Error ? error : new Error(String(error)));
      } finally {
        catchingUp = null;
      }
    })();
    return catchingUp;
  };

  const finishIfDone = () => {
    if (isFinished(view) && timer) {
      clearInterval(timer);
      timer = null;
      void connection?.stop();
    }
  };

  const start = async () => {
    const token = getAuth()?.token;
    connection = new HubConnectionBuilder()
      .withUrl(`${baseUrl()}/hub/jobs`, { accessTokenFactory: () => token ?? "" })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.None)
      .build();

    connection.on("jobEvent", (event: RunEvent) => {
      if (stopped || event.jobId !== jobId) return;
      if (apply([event])) void catchUp().then(finishIfDone);   // a gap: fetch what is missing
      finishIfDone();
    });
    connection.onreconnected(() => void catchUp().then(finishIfDone));

    try {
      await connection.start();
    } catch {
      // No push channel: the stored stream plus the safety poll below still gives the full run.
    }

    // The safety net: also covers a push channel that silently stopped delivering.
    timer = setInterval(() => {
      if (!stopped) void catchUp().then(finishIfDone);
    }, SAFETY_POLL_MS);

    await catchUp();
    finishIfDone();
  };

  void start();

  return {
    stop: async () => {
      stopped = true;
      if (timer) clearInterval(timer);
      if (connection && connection.state !== HubConnectionState.Disconnected) await connection.stop();
    },
  };
}
