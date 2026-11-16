import { Outlet } from "react-router";
import { ToastProvider } from "../providers/toast";
import { DialogProvider } from "../providers/dialog";
import { KeyboardLayerProvider } from "../providers/keyboard-layer";
import { ThemeProvider } from "../providers/theme";
import { ThemedRoot } from "./themed-root";
import { PromptConfigProvider } from "../providers/prompt-config";
import { ToolPermissionProvider } from "../providers/tool-permission";
import type { ReactNode } from "react";

/**
 * @param background Something that runs for the whole session inside the providers and renders nothing —
 * the real app passes the watcher that announces runs waiting for the operator; tests of single screens pass nothing.
 */
export function RootLayout({ background }: { background?: ReactNode }) {
  return (
    <ThemeProvider>
      <ToastProvider>
        <KeyboardLayerProvider>
          <DialogProvider>
            <PromptConfigProvider>
              <ToolPermissionProvider>
                <ThemedRoot>
                  {background}
                  <Outlet />
                </ThemedRoot>
              </ToolPermissionProvider>
            </PromptConfigProvider>
          </DialogProvider>
        </KeyboardLayerProvider>
      </ToastProvider>
    </ThemeProvider>
  );
};
