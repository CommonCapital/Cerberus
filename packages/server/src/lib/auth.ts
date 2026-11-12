import { db } from "@cerberus/database/client";

// Local, self-hosted authentication (replaces Clerk). Sessions are stored in
// the local database; the CLI keeps the opaque token in ~/.cerberus/auth.json.

const SESSION_TTL_MS = 1000 * 60 * 60 * 24 * 30; // 30 days

function generateToken(): string {
  return Buffer.from(crypto.getRandomValues(new Uint8Array(32))).toString("base64url");
}

type IssuedSession = {
  token: string;
  userId: string;
  expiresAt: Date;
};

async function createSession(userId: string): Promise<IssuedSession> {
  const token = generateToken();
  const expiresAt = new Date(Date.now() + SESSION_TTL_MS);
  await db.authSession.create({ data: { token, userId, expiresAt } });
  return { token, userId, expiresAt };
}

export async function registerUser(email: string, password: string): Promise<IssuedSession> {
  const existing = await db.user.findUnique({ where: { email } });
  if (existing) {
    throw new Error("A user with this email already exists");
  }

  const passwordHash = await Bun.password.hash(password);
  const user = await db.user.create({ data: { email, passwordHash } });
  return createSession(user.id);
}

export async function loginUser(email: string, password: string): Promise<IssuedSession | null> {
  const user = await db.user.findUnique({ where: { email } });
  if (!user) return null;

  const valid = await Bun.password.verify(password, user.passwordHash);
  if (!valid) return null;

  return createSession(user.id);
}

export async function validateSessionToken(token: string): Promise<{ userId: string } | null> {
  const session = await db.authSession.findUnique({ where: { token } });
  if (!session) return null;

  if (session.expiresAt.getTime() < Date.now()) {
    await db.authSession.delete({ where: { token } }).catch(() => {});
    return null;
  }

  return { userId: session.userId };
}

export async function revokeSessionToken(token: string): Promise<void> {
  await db.authSession.delete({ where: { token } }).catch(() => {});
}
