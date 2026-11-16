// Shared HTTP primitives. Keep these independent of endpoint collections so
// clients do not need to import one another (or their UI/test dependencies).
export const baseUrl = () => (process.env.API_URL ?? "http://localhost:3000").replace(/\/+$/, "");

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
  }
}
