---
name: nodejs-sandbox
description: Create Node.js applications, run JavaScript scripts, and execute TypeScript code in a sandbox environment using the node:22-slim Docker image. Use this skill whenever the user mentions Node.js, JavaScript execution, TypeScript, npm packages, or wants to create/run any kind of Node.js application, even if they don't explicitly say "Node.js sandbox."
---

# Node.js Sandbox Skill

A skill for creating and running Node.js applications in an isolated sandbox environment using the `node:22-slim` Docker image.

## When to Use This Skill

Use this skill when:
- User wants to create a Node.js application
- User wants to run JavaScript code/scripts
- User wants to execute TypeScript code
- User needs to test npm packages
- User wants to prototype a backend service
- User needs a sandboxed Node.js environment

---

## Docker Image Specification

**Image:** `node:22-slim`

This image provides:
- **Node.js v22** (latest LTS features)
- **npm** (Node Package Manager)
- **Minimal footprint** (Debian-based slim variant)
- **No build tools** (use multi-stage builds if native modules needed)

### Image Characteristics

| Feature | Status |
|---------|--------|
| Node.js Version | 22.x (LTS) |
| npm | Included |
| yarn | Not included (install if needed) |
| pnpm | Not included (install if needed) |
| Python/Build tools | Not included |
| Git | Not included |
| Size | ~200MB |

---

## Procedure

### Step 1: Create the Sandbox

Use the `sandbox_create` tool with the following configuration:

```json
{
  "image": "node:22-slim",
  "timeout_seconds": 600,
  "resource": {
    "cpu": "1",
    "memory": "2Gi"
  }
}
```

#### Resource Recommendations

| Use Case | CPU | Memory | Timeout |
|----------|-----|--------|---------|
| Simple scripts | 0.5 | 512Mi | 300s |
| Standard apps | 1 | 2Gi | 600s |
| Build processes | 2 | 4Gi | 900s |
| TypeScript compilation | 1 | 2Gi | 600s |

### Step 2: Initialize Project Structure

After sandbox creation, set up the project:

#### For JavaScript Projects

```bash
# Create project directory
mkdir -p /app

# Initialize npm project
cd /app && npm init -y

# Install dependencies as needed
npm install <package-name>
```

#### For TypeScript Projects

```bash
# Create project directory
mkdir -p /app

# Initialize npm project
cd /app && npm init -y

# Install TypeScript
npm install typescript @types/node --save-dev

# Create tsconfig.json
npx tsc --init

# Or use the template from this skill
```

### Step 3: Write Application Code

Use `file_write` to create application files:

#### JavaScript Example

```javascript
// /app/index.js
const express = require('express');
const app = express();
const port = 3000;

app.get('/', (req, res) => {
  res.json({ message: 'Hello from Node.js 22!' });
});

app.listen(port, () => {
  console.log(`Server running on port ${port}`);
});
```

#### TypeScript Example

```typescript
// /app/src/index.ts
import express from 'express';

const app = express();
const port: number = 3000;

app.get('/', (req, res) => {
  res.json({ message: 'Hello from TypeScript!' });
});

app.listen(port, () => {
  console.log(`Server running on port ${port}`);
});
```

### Step 4: Run the Application

#### JavaScript

```bash
node /app/index.js
```

#### TypeScript (with ts-node)

```bash
# Install ts-node
npm install ts-node --save-dev

# Run TypeScript directly
npx ts-node /app/src/index.ts
```

#### TypeScript (compiled)

```bash
# Compile TypeScript
npx tsc

# Run compiled JavaScript
node /app/dist/index.js
```

### Step 5: Expose Endpoints (for web apps)

Use `sandbox_get_endpoint` to get the public URL:

```json
{
  "sandbox_id": "<sandbox_id>",
  "port": 3000
}
```

---

## Project Templates

### Template 1: Basic JavaScript Project

```
/app/
├── package.json
├── index.js
└── node_modules/
```

**package.json:**
```json
{
  "name": "nodejs-app",
  "version": "1.0.0",
  "main": "index.js",
  "scripts": {
    "start": "node index.js"
  },
  "dependencies": {}
}
```

### Template 2: TypeScript Project

```
/app/
├── package.json
├── tsconfig.json
├── src/
│   └── index.ts
└── dist/
    └── index.js
```

**tsconfig.json:**
```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "NodeNext",
    "moduleResolution": "NodeNext",
    "outDir": "./dist",
    "rootDir": "./src",
    "strict": true,
    "esModuleInterop": true,
    "skipLibCheck": true
  },
  "include": ["src/**/*"],
  "exclude": ["node_modules"]
}
```

### Template 3: Express REST API

```
/app/
├── package.json
├── src/
│   ├── index.ts
│   ├── routes/
│   │   └── api.ts
│   └── middleware/
│       └── errorHandler.ts
└── dist/
```

---

## Common Operations

### Installing Packages

```bash
# Production dependencies
npm install express cors dotenv

# Development dependencies
npm install -D typescript @types/node @types/express

# Global packages (in sandbox context)
npm install -g nodemon
```

### Running Scripts

```bash
# Interactive Node.js REPL
node

# Run a single file
node script.js

# Run with environment variables
node -e "console.log(process.env.NODE_ENV)"
```

### Package Manager Alternatives

The `node:22-slim` image only includes npm. To use yarn or pnpm:

```bash
# Install yarn
npm install -g yarn

# Install pnpm
npm install -g pnpm
```

---

## Limitations & Workarounds

### No Native Module Build Tools

The slim image lacks Python and build tools. If you need native modules:

**Option 1: Use pre-built binaries**
```bash
# Many packages include pre-built binaries
npm install better-sqlite3  # Check if pre-built exists
```

**Option 2: Multi-stage build (advanced)**
Create a custom Dockerfile that includes build tools, then copies artifacts.

### No Git

Git is not included. For git operations:
- Clone repositories outside sandbox and copy files in
- Or install git: `apt-get update && apt-get install -y git`

### Memory Constraints

For memory-intensive operations:
- Increase sandbox memory allocation
- Use streaming for large file processing
- Implement pagination for large datasets

---

## Quick Reference Commands

| Task | Command |
|------|---------|
| Check Node version | `node --version` |
| Check npm version | `npm --version` |
| Initialize project | `npm init -y` |
| Install package | `npm install <package>` |
| Install dev package | `npm install -D <package>` |
| Run script | `node <file.js>` |
| Run TypeScript | `npx ts-node <file.ts>` |
| Compile TypeScript | `npx tsc` |
| List packages | `npm list` |
| Update packages | `npm update` |

---

## Export Checklist

When finished working in the sandbox:

1. ✅ Save all modified files using the `sandbox` skill
2. ✅ Export to `sandbox-files/` directory
3. ✅ Include package.json and package-lock.json
4. ✅ Document any environment variables needed
5. ✅ Note any external dependencies

---

## Success Criteria

- Sandbox created with `node:22-slim` image
- Node.js application runs successfully
- Dependencies installed correctly
- For web apps: endpoint accessible via sandbox URL
- Project exported to `sandbox-files/` if needed