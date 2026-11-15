---
name: sandbox-output
description: Ensure agents ALWAYS return sandbox_id and endpoint_url when deploying or running applications in a sandbox. Use this skill whenever an application is deployed, a server is started, or a web service is running in a sandbox environment. This skill guarantees consistent output formatting for sandbox deployments.
---

# Sandbox Output

This skill ensures that whenever an application is deployed or running in a sandbox, the agent **MUST** return a standardized output containing the sandbox identifier and the public endpoint URL.

## When to Use

Use this skill:
- After deploying a web application in a sandbox
- After starting a server or service in a sandbox
- After creating a static website hosted in a sandbox
- Whenever a sandbox is running an accessible application

## Required Output Format

When a sandbox application is running and accessible, **ALWAYS** return the following JSON structure:

```json
{
  "sandbox_id": "<sandbox UUID>",
  "endpoint_url": "<public URL to access the application>"
}
```

### Field Definitions

| Field | Type | Description |
|-------|------|-------------|
| `sandbox_id` | string (UUID) | The unique identifier of the sandbox returned by `sandbox_create` or `sandbox_connect` |
| `endpoint_url` | string (URL) | The public URL to access the running application, obtained via `sandbox_get_endpoint` |

## Procedure

### Step 1: Identify the Sandbox ID
After creating or connecting to a sandbox, capture the `sandbox_id` from the response.

### Step 2: Get the Endpoint URL
Use the `sandbox_get_endpoint` tool with the appropriate port number to retrieve the public URL.

```
sandbox_get_endpoint(sandbox_id="<sandbox_id>", port=<port_number>)
```

Common ports:
- `80` - HTTP web servers
- `443` - HTTPS web servers  
- `3000` - Node.js/React development servers
- `5000` - Flask/Python development servers
- `8000` - Django/Python development servers
- `8080` - Alternative HTTP port

### Step 3: Return the Standardized Output
Always conclude with the required JSON output format.

## Example

**Scenario:** A static website is deployed in a sandbox on port 80.

**Agent Actions:**
1. Create sandbox: `sandbox_create(image="python:3.11")` → returns `sandbox_id: "sbx_abc123"`
2. Start web server on port 80
3. Get endpoint: `sandbox_get_endpoint(sandbox_id="sbx_abc123", port=80)` → returns URL

**Required Output:**
```json
{
  "sandbox_id": "sbx_abc123",
  "endpoint_url": "https://abc123.example.com"
}
```

## Important Rules

1. **Never omit this output** - Users and other systems depend on this structured response to access the deployed application.

2. **Always use valid JSON** - The output must be parseable JSON with proper escaping.

3. **Verify the endpoint works** - Before returning, ensure the endpoint URL is accessible and the application is running.

4. **Include in final response** - This output should appear in the agent's final message to the user, not buried in intermediate steps.

## Success Criteria

- The response contains a valid `sandbox_id` (UUID format)
- The response contains a valid `endpoint_url` (accessible URL)
- The JSON is properly formatted and parseable
- The user can immediately access the application using the provided URL