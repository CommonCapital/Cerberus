---
name: sandbox
description: Export a generated project from a sandbox environment to the local `sandbox-files` directory while **strictly preserving the exact folder structure** from the sandbox.
---

## When to Use
Use this skill after a project has been generated or modified inside the sandbox and needs to be saved locally for external access.

## Procedure

### 1. Discover Project Structure
Use the `file_search` tool (or `ls`) to identify the **Source Root** (e.g., `/app` or `/home/user/project`) and discover all files within it. 

### 2. Map Destination Paths
For every file found in the sandbox, the agent must calculate the local destination path using this logic:
* **Formula:** `sandbox-files/` + `[Relative path from Sandbox Source Root]`
* **Example:** `/app/src/main.js` becomes `sandbox-files/src/main.js`

### 3. Execution (Read-Create-Write)
For **each** file identified in the discovery phase, follow these steps in order:

1. **Read:** Use `file_read` to get the content from the sandbox.
2. **Prepare Directory:** Use `file_create_directories` to ensure the sub-folders exist in `sandbox-files` **before** writing.
3. **Write:** Use `write_file` to save the content to the mapped local path.



---

## Directory Rules & Constraints
* **Never Flatten:** Files must stay in their original sub-folders. Do not dump all files into the root of `sandbox-files`.
* **Mirror Exactly:** The folder hierarchy in `sandbox-files` must be a 1:1 clone of the sandbox project.
* **Pre-empt Errors:** Always check if the local directory exists using `ls` before the first write; if not, create `sandbox-files` first.

---

## Export Summary
After the export is finished, return a summary in the following format:

**Export Status:** Completed Successfully
**Total Files Exported:** [Number]
**Sandbox Root:** [Path]
**Local Export Path:** ./sandbox-files/

---

## Success Criteria
The `sandbox-files` directory contains a complete, structured copy of the sandbox project. A user navigating `sandbox-files` should see the exact same layout as seen inside the sandbox environment.