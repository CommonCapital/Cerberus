import dotenv from "dotenv";
import path from "path";
import { PrismaLibSql } from "@prisma/adapter-libsql";
import { PrismaClient } from "../generated/prisma/client.ts";

const repoRoot = path.resolve(import.meta.dirname, "../../..");

dotenv.config({ path: path.join(repoRoot, ".env") });

// Resolve a relative SQLite file: URL against the repo root so the database
// location is the same no matter which working directory the process runs in.
export function resolveSqliteUrl(raw: string | undefined): string {
  const value = raw ?? "file:./cerberus.db";
  const match = /^file:(.*)$/.exec(value);
  if (!match) return value;

  const filePath = match[1]!;
  if (filePath.startsWith("/") || filePath.startsWith(":")) return value; // absolute or :memory:
  return `file:${path.resolve(repoRoot, filePath)}`;
}

const adapter = new PrismaLibSql({ url: resolveSqliteUrl(process.env.DATABASE_URL) });

export const db = new PrismaClient({ adapter });
