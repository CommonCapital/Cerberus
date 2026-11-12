import { apiClient } from "./api-client";
import { saveAuth } from "./auth";
import { getErrorMessage } from "./http-errors";

// Local authentication (replaces the Clerk browser OAuth flow). For a fully
// local, single-operator setup we use credentials from the environment with
// sensible defaults, auto-registering the account on first login. A richer
// interactive credential prompt can be added later.
const DEFAULT_EMAIL = "local@cerberus.local";
const DEFAULT_PASSWORD = "local-dev-password";

/**
 * The default account's address before the tool was renamed. An installation that was used under the old
 * name has its conversations, scans and settings under this account, so it is tried before a new one is made.
 */
const FORMER_DEFAULT_EMAIL = "local@nightcode.dev";

export async function performLogin(): Promise<{ token: string }> {
  const email = process.env.CERBERUS_EMAIL ?? DEFAULT_EMAIL;
  const password = process.env.CERBERUS_PASSWORD ?? DEFAULT_PASSWORD;
  const json = { email, password };

  // Try to sign in; if the account does not exist yet, create it.
  const login = await apiClient.auth.login.$post({ json });
  if (login.ok) {
    return persistToken(await login.json());
  }
  if (login.status !== 401) {
    throw new Error(await getErrorMessage(login));
  }

  // With the defaults in use: an account from before the rename keeps being the operator's account.
  if (!process.env.CERBERUS_EMAIL) {
    const former = await apiClient.auth.login.$post({ json: { email: FORMER_DEFAULT_EMAIL, password } });
    if (former.ok) {
      return persistToken(await former.json());
    }
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
