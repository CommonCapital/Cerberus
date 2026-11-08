import { apiClient } from "./api-client";
import { saveAuth } from "./auth";
import { getErrorMessage } from "./http-errors";

// Local authentication (replaces the Clerk browser OAuth flow). For a fully
// local, single-operator setup we use credentials from the environment with
// sensible defaults, auto-registering the account on first login. A richer
// interactive credential prompt can be added later.
const DEFAULT_EMAIL = "local@nightcode.dev";
const DEFAULT_PASSWORD = "local-dev-password";

export async function performLogin(): Promise<{ token: string }> {
  const email = process.env.NIGHTCODE_EMAIL ?? DEFAULT_EMAIL;
  const password = process.env.NIGHTCODE_PASSWORD ?? DEFAULT_PASSWORD;
  const json = { email, password };

  // Try to sign in; if the account does not exist yet, create it.
  const login = await apiClient.auth.login.$post({ json });
  if (login.ok) {
    return persistToken(await login.json());
  }
  if (login.status !== 401) {
    throw new Error(await getErrorMessage(login));
  }

  const register = await apiClient.auth.register.$post({ json });
  if (!register.ok) {
    throw new Error(await getErrorMessage(register));
  }
  return persistToken(await register.json());
}

function persistToken(data: unknown): { token: string } {
  const token = (data as { token?: unknown }).token;
  if (typeof token !== "string") {
    throw new Error("Login response did not include a token");
  }
  saveAuth({ token });
  return { token };
}
