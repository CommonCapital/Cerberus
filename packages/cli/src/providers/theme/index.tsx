import { readPreferences, savePreference } from "../../lib/preferences";
import { createContext, useContext, useState, useCallback } from "react";
import type { ReactNode } from "react";
import type { ThemeColors, Theme } from "../../theme";
import { DEFAULT_THEME, THEMES } from "../../theme";


function getInitialTheme(): Theme {
  return THEMES.find((theme) => theme.name === readPreferences().themeName) ?? DEFAULT_THEME;
};

function persistTheme(theme: Theme) {
  savePreference("themeName", theme.name);
};

type ThemeContextValue = {
  colors: ThemeColors;
  currentTheme: Theme;
  setTheme: (theme: Theme) => void;
};

const ThemeContext = createContext<ThemeContextValue | null>(null);

export function useTheme(): ThemeContextValue {
  const value = useContext(ThemeContext);
  if (!value) {
    throw new Error("useTheme must be used within a ThemeProvider");
  }
  return value;
}

type ThemeProviderProps = {
  children: ReactNode;
};

export function ThemeProvider({ children }: ThemeProviderProps) {
  const [currentTheme, setCurrentTheme] = useState<Theme>(getInitialTheme);

  const setTheme = useCallback((theme: Theme) => {
    setCurrentTheme(theme);
    persistTheme(theme);
  }, []);

  return (
    <ThemeContext.Provider 
      value={{ colors: currentTheme.colors, currentTheme, setTheme }}>
      {children}
    </ThemeContext.Provider>
  );
};
