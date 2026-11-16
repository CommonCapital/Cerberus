import { apiClient } from "./api-client";
import { saveAuth } from "./auth";
import { getErrorMessage } from "./http-errors";

// Local authentication. For a fully
// local, single-operator setup we use credentials from the environment with
// sensible defaults, auto-registering the account on first login. A richer
// interactive credential prompt can be added later.
const DEFAULT_EMAIL = "local@cerberus.local";
const DEFAULT_PASSWORD = "local-dev-password";

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
  const email = deps.email() ?? DEFAULT_EMAIL;
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
