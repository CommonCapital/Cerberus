import { SUPPORTED_CHAT_MODELS } from "@nightcode/shared";
import { 
  AgentsDialogContent,
  ModelsDialogContent,
  AbortDialogContent,
  AssessDialogContent,
  AssessmentsDialogContent,
  EngagementDialogContent,
  MemoryDialogContent,
  ScansDialogContent,
  ScopeDialogContent,
  ThreatModelDialogContent,
  PermissionsDialogContent,
  AgentReviewDialogContent,
  ReviewDialogContent,
  RunsDialogContent,
  ScanDialogContent,
  SessionsDialogContent,
  ThemeDialogContent,
} from "../dialogs";
import type { Command } from "./types";

import { performLogin } from "../../lib/oauth";
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
    description: "Select AI model for generation",
    value: "/models",
    action: (ctx) => {
      ctx.dialog.open({
        title: "Select Model",
        children: (
          <ModelsDialogContent
            models={SUPPORTED_CHAT_MODELS.map((model) => model.id)}
            onSelectModel={ctx.setModel}
          />
        ),
      })
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
