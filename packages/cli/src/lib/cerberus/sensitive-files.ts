// Which file names probably hold credentials. Used wherever a rule would
// otherwise let such a file be read without the operator being asked.

/** Files that usually hold credentials. */
const SECRET_FILE = /^(\.env(\..+)?|\.netrc|\.npmrc|\.pypirc|id_(rsa|dsa|ecdsa|ed25519)|.+\.(pem|key|p12|pfx|keystore))$/i;
/** …unless it is only a template of one (`.env.example`). */
const TEMPLATE_FILE = /\.(example|sample|template|dist|defaults)$/i;

/** Whether a path names a file that probably holds secrets, judged by its last segment. */
export function looksSecret(path: string): boolean {
  const name = path.split(/[\\/]/).pop() ?? "";
  return SECRET_FILE.test(name) && !TEMPLATE_FILE.test(name);
}

/** Whether a path goes through a directory that holds credentials (`~/.ssh/config`, `.aws/credentials`). */
export function inSecretDirectory(path: string): boolean {
  return /(^|[\\/])\.(ssh|aws|gnupg|kube|docker)([\\/]|$)/.test(path) || /(^|[\\/])credentials$/i.test(path);
}
