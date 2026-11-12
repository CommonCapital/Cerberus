import { 
  AgentsDialogContent,
  ModelsDialogContent,
  AbortDialogContent,
  AssessDialogContent,
  AssessmentsDialogContent,
  EngagementDialogContent,
  IndexDialogContent,
  MemoryDialogContent,
  ScansDialogContent,
  ScopeDialogContent,
  ThreatModelDialogContent,
  TeamReviewDialogContent,
  PermissionsDialogContent,
  AgentReviewDialogContent,
  ReviewDialogContent,
  RunsDialogContent,
  SchedulesDialogContent,
  KeysDialogContent,
  ScanDialogContent,
  SessionsDialogContent,
  ForkDialogContent,
  RenameDialogContent,
  ThemeDialogContent,
} from "../dialogs";
import type { Command } from "./types";

import { performLogin } from "../../lib/oauth";
import { cerberus } from "../../lib/cerberus/api";
import { requestUndo, type UndoKind } from "../../lib/undo-events";
import { saveTranscript } from "../../lib/session-housekeeping";
import { transcriptIo } from "../../lib/session-housekeeping-io";
import type { CommandContext } from "./types";

/** "/undo" and "/redo": asked of the conversation on screen, which carries it out. */
const undoOrRedo = (kind: UndoKind) => (ctx: CommandContext) => {
  if (!ctx.sessionId || !requestUndo(ctx.sessionId, kind)) {
    ctx.toast.show({ variant: "error", message: `Open the conversation whose changes you want to ${kind}; /${kind} works on the one you are in.` });
  }
};
import { clearAuth } from "../../lib/auth";

export const COMMANDS: Command[] = [
  {
    name: "new",
    description: "Start a new conversation",
    value: "/new",
    action: (ctx) => {
      ctx.navigate("/");
    },
  },
  {
    name: "compact",
    description: "Summarise the earlier part of this conversation to make room",
    value: "/compact",
    action: async (ctx) => {
      if (!ctx.sessionId) {
        ctx.toast.show({ variant: "error", message: "Open a conversation first; /compact works on the one you are in." });
        return;
      }
      ctx.toast.show({ variant: "info", message: "Summarising the earlier part of the conversation…" });
      try {
        const result = await cerberus.compactSession(ctx.sessionId, ctx.model);
        ctx.toast.show({
          variant: "info",
          message: `Summarised ${result.messages} earlier message${result.messages === 1 ? "" : "s"}. They stay on screen; the model now works from the summary.`,
        });
      } catch (problem) {
        ctx.toast.show({ variant: "error", message: problem instanceof Error ? problem.message : "Could not compact the conversation" });
      }
    },
  },
  {
    name: "undo",
    description: "Put back the files the agent changed in its last turn",
    value: "/undo",
    action: undoOrRedo("undo"),
  },
  {
    name: "redo",
    description: "Re-apply the changes you just undid",
    value: "/redo",
    action: undoOrRedo("redo"),
  },
  {
    name: "agents",
    description: "Switch agents",
    value: "/agents",
    action: (ctx) => {
      ctx.dialog.open({
        title: "Select Agent",
        children: <AgentsDialogContent currentMode={ctx.mode} onSelectMode={ctx.setMode} />,
      })
    },
  },
  {
    name: "models",
    description: "Choose the model: Claude, GPT or DeepSeek by API, or a local one from Ollama",
    value: "/models",
    action: (ctx) => {
      ctx.dialog.open({
        title: "Select Model",
        children: <ModelsDialogContent onSelectModel={ctx.setModel} />,
      })
    },
  },
  {
    name: "keys",
    description: "See which API providers have a key stored, or remove one",
    value: "/keys",
    action: (ctx) => {
      ctx.dialog.open({ title: "API keys", children: <KeysDialogContent /> });
    },
  },
  {
    name: "scan",
    description: "Scan code for security issues and watch the run live",
    value: "/scan",
    action: (ctx) => {
      ctx.dialog.open({
        title: "Scan",
        children: <ScanDialogContent />,
      })
    },
  },
  {
    name: "review",
    description: "Plan a full security review, approve it, and get a report",
    value: "/review",
    action: (ctx) => {
      ctx.dialog.open({
        title: "Security review",
        children: <ReviewDialogContent model={ctx.model} />,
      })
    },
  },
  {
    name: "sast",
    description: "Have the current model read the code and report vulnerabilities",
    value: "/sast",
    action: (ctx) => {
      ctx.dialog.open({
        title: `Code review by ${ctx.model}`,
        children: <AgentReviewDialogContent model={ctx.model} />,
      })
    },
  },
  {
    name: "threatmodel",
    description: "Have the current model map the code's components and threats",
    value: "/threatmodel",
    action: (ctx) => {
      ctx.dialog.open({
        title: `Threat model by ${ctx.model}`,
        children: <ThreatModelDialogContent model={ctx.model} />,
      })
    },
  },
  {
    name: "team",
    description: "Have a lead agent send specialist agents and merge what they find",
    value: "/team",
    action: (ctx) => {
      ctx.dialog.open({
        title: `Team review by ${ctx.model}`,
        children: <TeamReviewDialogContent model={ctx.model} />,
      })
    },
  },
  {
    name: "index",
    description: "Index code for search by meaning (local embedding model)",
    value: "/index",
    action: (ctx) => {
      ctx.dialog.open({
        title: "Semantic index",
        children: <IndexDialogContent />,
      })
    },
  },
  {
    name: "assess",
    description: "Check code against a compliance catalog",
    value: "/assess",
    action: (ctx) => {
      ctx.dialog.open({
        title: "Compliance assessment",
        children: <AssessDialogContent />,
      })
    },
  },
  {
    name: "assessments",
    description: "Open an assessment: controls, suggested answers, your answers",
    value: "/assessments",
    action: (ctx) => {
      ctx.dialog.open({
        title: "Assessments",
        children: <AssessmentsDialogContent />,
      })
    },
  },
  {
    name: "findings",
    description: "Open a scan's findings: accept a risk, mark a false positive",
    value: "/findings",
    action: (ctx) => {
      ctx.dialog.open({
        title: "Scans",
        children: <ScansDialogContent />,
      })
    },
  },
  {
    name: "runs",
    description: "Open a run: watch it, approve it, answer its questions",
    value: "/runs",
    action: (ctx) => {
      ctx.dialog.open({
        title: "Runs",
        children: <RunsDialogContent />,
      })
    },
  },
  {
    name: "schedules",
    description: "See the recurring runs; pause one or set it running again",
    value: "/schedules",
    action: (ctx) => {
      ctx.dialog.open({ title: "Schedules", children: <SchedulesDialogContent /> });
    },
  },
  {
    name: "permissions",
    description: "Choose what runs without asking you first",
    value: "/permissions",
    action: (ctx) => {
      ctx.dialog.open({
        title: "Permissions",
        children: <PermissionsDialogContent />,
      })
    },
  },
  {
    name: "engagement",
    description: "Choose or create the engagement your runs belong to",
    value: "/engagement",
    action: (ctx) => {
      ctx.dialog.open({
        title: "Engagement",
        children: <EngagementDialogContent />,
      })
    },
  },
  {
    name: "scope",
    description: "See or change what the current engagement may be run on",
    value: "/scope",
    action: (ctx) => {
      ctx.dialog.open({
        title: "Scope",
        children: <ScopeDialogContent />,
      })
    },
  },
  {
    name: "abort",
    description: "Kill switch: stop everything under the current engagement",
    value: "/abort",
    action: (ctx) => {
      ctx.dialog.open({
        title: "Abort engagement",
        children: <AbortDialogContent />,
      })
    },
  },
  {
    name: "memory",
    description: "See, add or forget what agents remember between runs",
    value: "/memory",
    action: (ctx) => {
      ctx.dialog.open({
        title: "Memory",
        children: <MemoryDialogContent />,
      })
    },
  },
  {
    name: "sessions",
    description: "Browse past sessions",
    value: "/sessions",
    action: (ctx) => {
      ctx.dialog.open({
        title: "Sessions",
        children: <SessionsDialogContent />,
      })
    },
  },
  {
    name: "fork",
    description: "Copy this conversation, whole or up to an earlier reply, and carry on in the copy",
    value: "/fork",
    action: (ctx) => {
      if (!ctx.sessionId) {
        ctx.toast.show({ variant: "error", message: "Open a conversation first; /fork copies the one you are in." });
        return;
      }
      ctx.dialog.open({ title: "Fork this conversation", children: <ForkDialogContent sessionId={ctx.sessionId} /> });
    },
  },
  {
    name: "rename",
    description: "Give this conversation a name of your own",
    value: "/rename",
    action: (ctx) => {
      if (!ctx.sessionId) {
        ctx.toast.show({ variant: "error", message: "Open a conversation first; /rename names the one you are in." });
        return;
      }
      ctx.dialog.open({ title: "Rename this conversation", children: <RenameDialogContent sessionId={ctx.sessionId} /> });
    },
  },
  {
    name: "export",
    description: "Save this conversation as a Markdown file in the current directory",
    value: "/export",
    action: async (ctx) => {
      if (!ctx.sessionId) {
        ctx.toast.show({ variant: "error", message: "Open a conversation first; /export saves the one you are in." });
        return;
      }
      try {
        const path = await saveTranscript(ctx.sessionId, transcriptIo);
        ctx.toast.show({ variant: "info", message: `Saved to ${path}. Credential-shaped values are masked.` });
      } catch (problem) {
        ctx.toast.show({ variant: "error", message: problem instanceof Error ? problem.message : "Could not export the conversation" });
      }
    },
  },
  {
    name: "theme",
    description: "Change color theme",
    value: "/theme",
    action: (ctx) => {
      ctx.dialog.open({
        title: "Select Theme",
        children: <ThemeDialogContent />,
      })
    },
  },
  {
    name: "login",
    description: "Sign in to the local server",
    value: "/login",
    action: async (ctx) => {
      ctx.toast.show({ message: "Signing in locally..." });

      try {
        await performLogin();
        ctx.toast.show({ variant: "success", message: "Signed in" });
      } catch (error) {
        const message = error instanceof Error 
          ? error.message 
          : "Sign in failed or timed out";

        ctx.toast.show({ variant: "error", message });
      }
    },
  },
  {
    name: "logout",
    description: "Sign out of your account",
    value: "/logout",
    action: (ctx) => {
      clearAuth();
      ctx.toast.show({ variant: "success", message: "Signed out" });
    },
  },
  {
    name: "exit",
    description: "Quit the application",
    value: "/exit",
    action: (ctx) => {
      ctx.exit();
    },
  },
];
