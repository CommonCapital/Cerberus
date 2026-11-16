import { createMiddleware } from "hono/factory";
import { validateSessionToken } from "../lib/auth";

export type AuthenticatedEnv = {
  Variables: {
    userId: string;
  };
};

function extractBearerToken(header: string | undefined): string | null {
  if (!header) return null;
  return header.startsWith("Bearer ") ? header.slice(7).trim() || null : null;
}

export const requireAuth = createMiddleware<AuthenticatedEnv>(async (c, next) => {
  try {
    const token = extractBearerToken(c.req.header("Authorization"));
    if (!token) {
      return c.json({ error: "Unauthorized. Run /login to continue." }, 401);
    }

    const auth = await validateSessionToken(token);
    if (!auth) {
      return c.json({ error: "Unauthorized. Run /login to continue." }, 401);
    }

    c.set("userId", auth.userId);
    await next();
  } catch {
    return c.json({ error: "Unauthorized. Run /login to continue." }, 401);
  }
});
