import { Hono } from "hono";
import { zValidator } from "@hono/zod-validator";
import { z } from "zod";

import { registerUser, loginUser, revokeSessionToken } from "../lib/auth";

const credentialsSchema = z.object({
  email: z.string().email(),
  password: z.string().min(6, "Password must be at least 6 characters"),
});

const credentialsValidator = zValidator("json", credentialsSchema, (result, c) => {
  if (!result.success) {
    return c.json({ error: "A valid email and a password (min 6 characters) are required" }, 400);
  }
});

const app = new Hono()
  .post("/register", credentialsValidator, async (c) => {
    const { email, password } = c.req.valid("json");
    try {
      const { token } = await registerUser(email, password);
      return c.json({ token }, 201);
    } catch (error) {
      const message = error instanceof Error ? error.message : "Registration failed";
      return c.json({ error: message }, 409);
    }
  })
  .post("/login", credentialsValidator, async (c) => {
    const { email, password } = c.req.valid("json");
    const result = await loginUser(email, password);
    if (!result) {
      return c.json({ error: "Invalid email or password" }, 401);
    }
    return c.json({ token: result.token });
  })
  .post("/logout", async (c) => {
    const header = c.req.header("Authorization");
    const token = header?.startsWith("Bearer ") ? header.slice(7).trim() : null;
    if (token) {
      await revokeSessionToken(token);
    }
    return c.json({ success: true });
  });

export default app;
