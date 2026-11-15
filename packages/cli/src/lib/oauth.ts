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

export type LoginDeps = Pick<typeof apiClient, "login" | "register"> & {
  saveAuth: typeof saveAuth;
  email: () => string | undefined;
  password: () => string | undefined;
};

const loginIo: LoginDeps = {
  login: apiClient.login,
  register: apiClient.register,
  saveAuth,
  email: () => process.env.CERBERUS_EMAIL,
  password: () => process.env.CERBERUS_PASSWORD,
};

export async function performLogin(deps: LoginDeps = loginIo): Promise<{ token: string }> {
  const configuredEmail = deps.email();
  const email = configuredEmail ?? DEFAULT_EMAIL;
  const password = deps.password() ?? DEFAULT_PASSWORD;
  const json = { email, password };

  // Try to sign in; if the account does not exist yet, create it.
  const login = await deps.login(json);
  if (login.ok) {
    return persistToken(await login.json(), deps.saveAuth);
  }
  if (login.status !== 401) {
    throw new Error(await getErrorMessage(login));
  }

  // With the defaults in use: an account from before the rename keeps being the operator's account.
  if (!configuredEmail) {
    const former = await deps.login({ email: FORMER_DEFAULT_EMAIL, password });
    if (former.ok) {
      return persistToken(await former.json(), deps.saveAuth);
    }
    if (former.status !== 401) throw new Error(await getErrorMessage(former));
  }

  const register = await deps.register(json);
  if (!register.ok) {
    throw new Error(await getErrorMessage(register));
  }
  return persistToken(await register.json(), deps.saveAuth);
}

function persistToken(data: unknown, save: typeof saveAuth): { token: string } {
  const token = data && typeof data === "object" ? (data as { token?: unknown }).token : undefined;
  if (typeof token !== "string" || token.trim().length === 0) {
    throw new Error("Login response did not include a token");
  }
  save({ token });
  return { token };
}
