---
name: project-organization
description: MANDATORY FIRST SKILL for all project-building tasks. This skill MUST be read and executed BEFORE any app-building skill (flask-app-builder, web-artifacts-builder, etc.). Use when user asks to build, create, or develop ANY application, website, API, or coding project. Ensures each project has its own isolated working folder with organized structure.
---

# Project Organization Skill

## Overview

This skill establishes a **project isolation standard** that ensures every project the user requests has its own dedicated working folder. This prevents file conflicts, maintains clean organization, and allows continuous work on multiple projects.

## ⚠️ CRITICAL RULE: READ THIS SKILL FIRST

**Before building ANY project (Flask app, React app, API, website, etc.), you MUST:**

1. ✅ Read this skill completely
2. ✅ Create a dedicated project folder
3. ✅ Organize all project files inside that folder
4. ✅ Store all todos and project-related files in the same folder

## When to Use This Skill

This skill triggers automatically when:
- User says "build me a [X] app"
- User says "create a [X] application"
- User says "make a [X] website"
- User asks for any coding project creation
- User wants to continue work on an existing project

## Project Folder Structure

```
projects/
├── project-name-1/
│   ├── README.md               # ⭐ MANDATORY: Project overview, setup instructions, usage
│   ├── project.md              # Project description and metadata
│   ├── todos/                  # Project-specific todo lists
│   │   └── project-todo.todos.json
│   ├── src/                    # Source code
│   ├── tests/                  # Test files
│   ├── docs/                   # Documentation
│   ├── sandbox-files/          # Exported sandbox files
│   └── requirements.txt        # Dependencies (if applicable)
│
├── project-name-2/
│   ├── README.md
│   ├── project.md
│   ├── todos/
│   ├── src/
│   └── ...
│
└── .project-registry.json      # Master registry of all projects
```

### ⭐ README.md Requirement

**Every project folder MUST contain a README.md file.** This file serves as:

1. **Quick Reference:** What the project does and how to use it
2. **Setup Guide:** Installation and configuration steps
3. **Context Preservation:** Enables any agent to understand the project quickly
4. **Documentation Hub:** Links to detailed docs and resources

**README.md Template:**
```markdown
# {Project Name}

## Overview
Brief description of what this project does and its purpose.

## Quick Start
```bash
# Installation
pip install -r requirements.txt

# Run
python src/app.py
```

## Project Structure
```
├── src/           # Source code
├── tests/         # Test files
├── docs/          # Documentation
└── sandbox-files/ # Exported sandbox files
```

## Technology Stack
- Framework: {Flask/React/etc.}
- Language: {Python/JavaScript/TypeScript}
- Dependencies: {List main dependencies}

## URLs
- Local: http://localhost:{PORT}
- Sandbox: {Sandbox URL if deployed}

## Created
- Date: {YYYY-MM-DD}
- User: {Username}

## Notes
Any additional notes or important information.
```

## Why README.md is MANDATORY

1. **Self-Documenting:** Anyone (including future you) can understand the project
2. **Quick Context:** Agents can read README.md to understand project scope
3. **Professional Standard:** Follows industry best practices
4. **Onboarding:** Makes it easy to resume work after breaks

## Step-by-Step Workflow

### Step 1: Check for Existing Projects

Before creating a new project, check if the `projects/` folder exists:

```bash
# Check if projects folder exists
ls projects/
```

If it doesn't exist, create it.

### Step 2: Generate Project Folder Name

Create a descriptive folder name based on the project:

| User Request | Project Folder Name |
|--------------|---------------------|
| "build me a Flask app" | `projects/flask-app-001/` |
| "create a todo app" | `projects/todo-app/` |
| "make an e-commerce site" | `projects/ecommerce-site/` |
| "build a REST API" | `projects/rest-api-001/` |

**Naming Rules:**
- Use lowercase with hyphens
- Be descriptive but concise
- Add numbers if similar projects exist (e.g., `flask-app-002`)

### Step 3: Create Project Folder Structure

Create the standard folder structure:

```bash
# Create main project folder
mkdir -p projects/{project-name}

# Create subfolders
mkdir -p projects/{project-name}/todos
mkdir -p projects/{project-name}/src
mkdir -p projects/{project-name}/docs
mkdir -p projects/{project-name}/sandbox-files
```

### Step 4: Create README.md (MANDATORY)

Create `README.md` with project overview and setup instructions:

```markdown
# {Project Name}

## Overview
{Brief description of what this project does and its purpose}

## Quick Start
```bash
# Installation
pip install -r requirements.txt

# Run
python src/app.py
```

## Project Structure
```
├── src/           # Source code
├── tests/         # Test files
├── docs/          # Documentation
└── sandbox-files/ # Exported sandbox files
```

## Technology Stack
- Framework: {Flask/React/etc.}
- Language: {Python/JavaScript/TypeScript}
- Dependencies: {List main dependencies}

## URLs
- Local: http://localhost:{PORT}
- Sandbox: {Sandbox URL if deployed}

## Created
- Date: {YYYY-MM-DD}
- User: {Username}

## Notes
{Any additional notes}
```

### Step 5: Create Project Metadata File

Create `project.md` with project information:

```markdown
# Project: {Project Name}

## Metadata
- **Created:** {DATE}
- **User:** {Username}
- **Type:** {Flask App / React App / API / etc.}
- **Status:** {In Progress / Completed / Paused}

## Description
{Brief description of what the project does}

## Technology Stack
- Framework: {Flask / React / etc.}
- Language: {Python / JavaScript / TypeScript}
- Dependencies: {List main dependencies}

## Project Structure
{Overview of folder structure}

## Endpoints / URLs
- Local: http://localhost:{PORT}
- Sandbox: {Sandbox URL if deployed}

## Notes
{Any additional notes}
```

### Step 6: Create Project-Specific Todo List

Create the todo list INSIDE the project folder:

```json
// projects/{project-name}/todos/project-todo.todos.json
{
  "workflow_id": "{project-name}-workflow",
  "tasks": [
    {
      "task": "Set up project structure",
      "assigned_to": "me",
      "status": "completed"
    },
    // ... more tasks
  ]
}
```

### Step 7: Update Project Registry

Update the master registry at `projects/.project-registry.json`:

```json
{
  "projects": [
    {
      "name": "flask-app-001",
      "path": "projects/flask-app-001",
      "type": "Flask App",
      "created": "2026-03-11",
      "status": "in_progress"
    }
  ]
}
```

### Step 8: Proceed with Project-Specific Skill

After the folder structure is ready, NOW proceed to use the appropriate skill:

- For Flask apps → Use `flask-app-builder` skill
- For React apps → Use `web-artifacts-builder` skill
- For APIs → Use `claude-api` skill
- etc.

---

## 📋 Todo Sync Protocol (Sub-Action)

*Automatically syncs the todo list when files or folders are created/deleted within a project.*

### Overview

The **Todo Sync Protocol** is a sub-action that automatically tracks task completion based on file/folder operations in the project. It integrates with the existing workflow to keep the todo list in sync with actual project progress.

### How It Works

This sub-action runs automatically whenever files or folders are created or deleted within a project folder. It reads the current todo list, identifies matching tasks, and updates their status accordingly.

### Triggers & Behaviors

| Trigger | Behavior | Example |
|---------|----------|---------|
| **File created** in `src/` | Mark matching task as `completed` | Create `app.py` → marks "Create main app file" as completed |
| **File created** in `tests/` | Mark matching test task as `completed` | Create `test_app.py` → marks "Write tests" as completed |
| **File created** in `docs/` | Mark matching doc task as `completed` | Create `README.md` → marks "Create documentation" as completed |
| **Folder created** | Mark matching folder creation task as `completed` | Create `src/components/` → marks "Create component folder" as completed |
| **File deleted** | Mark matching task as `cancelled` | Delete `app.py` → marks "Create main app file" as cancelled |
| **Folder deleted** | Mark matching task as `cancelled` | Delete `src/utils/` → marks "Create utils folder" as cancelled |

### Matching Logic

The sync uses **fuzzy matching** to connect files/folders to tasks:

1. **Exact match:** Task text contains the filename exactly
2. **Partial match:** Task text contains key parts of the filename
3. **Category match:** Task relates to the folder type (`src/`, `tests/`, `docs/`)

**Priority:** Exact match > Partial match > Category match

### Integration Points

#### Integration Point 1: After Step 3 (Folder Creation)

After creating the project folder structure, initialize the todo list with tasks that map to the created folders:

```json
{
  "workflow_id": "flask-app-001-workflow",
  "tasks": [
    {"task": "Create src/ folder", "assigned_to": "me", "status": "pending"},
    {"task": "Create tests/ folder", "assigned_to": "me", "status": "pending"},
    {"task": "Create docs/ folder", "assigned_to": "me", "status": "pending"}
  ]
}
```

#### Integration Point 2: During Project Building

Whenever you create a file or folder during project work, run the sync:

```
ACTION: track-todo
- project: {project-name}
- operation: create
- path: {relative-path-from-project-root}
- type: file | folder
```

**Auto-execution:** The agent should automatically run this sub-action after any file/folder creation operation within a project.

#### Integration Point 3: After File Deletion

When deleting files (as part of refactoring or cleanup), update the todo:

```
ACTION: track-todo
- project: {project-name}
- operation: delete
- path: {relative-path-from-project-root}
- type: file | folder
```

### Example Scenarios

#### Scenario 1: Building a Flask App

```
Project: flask-landing-page
Initial Todo:
[
  {"task": "Create src/ folder", "status": "pending"},
  {"task": "Create app.py", "status": "pending"},
  {"task": "Create templates/index.html", "status": "pending"},
  {"task": "Write tests", "status": "pending"}
]

Agent Actions:
1. mkdir projects/flask-landing-page/src
   → Sync: "Create src/ folder" → "completed"

2. write_file projects/flask-landing-page/src/app.py
   → Sync: "Create app.py" → "completed"

3. write_file projects/flask-landing-page/templates/index.html
   → Sync: "Create templates/index.html" → "completed"

4. write_file projects/flask-landing-page/tests/test_app.py
   → Sync: "Write tests" → "completed"

Final Todo:
[
  {"task": "Create src/ folder", "status": "completed"},
  {"task": "Create app.py", "status": "completed"},
  {"task": "Create templates/index.html", "status": "completed"},
  {"task": "Write tests", "status": "completed"}
]
```

#### Scenario 2: Refactoring - Deleting Files

```
Current Todo:
[
  {"task": "Create old_util.py", "status": "completed"},
  {"task": "Create new_util.py", "status": "pending"}
]

Agent Actions:
1. delete projects/myapp/src/old_util.py
   → Sync: "Create old_util.py" → "cancelled"

2. write_file projects/myapp/src/new_util.py
   → Sync: "Create new_util.py" → "completed"

Final Todo:
[
  {"task": "Create old_util.py", "status": "cancelled"},
  {"task": "Create new_util.py", "status": "completed"}
]
```

#### Scenario 3: Folder Creation

```
Current Todo:
[
  {"task": "Create components folder", "status": "pending"},
  {"task": "Create Button component", "status": "pending"}
]

Agent Actions:
1. mkdir projects/react-app/src/components
   → Sync: "Create components folder" → "completed"

2. write_file projects/react-app/src/components/Button.tsx
   → Sync: "Create Button component" → "completed"
```

### Manual Override

If automatic sync doesn't capture the correct task, manually update:

```json
{
  "task": "Create complex feature",
  "assigned_to": "me",
  "status": "completed",
  "note": "Manually marked complete - feature implemented across multiple files"
}
```

### Backward Compatibility

The Todo Sync Protocol is **opt-in** and doesn't break existing workflows:

- Existing projects without sync-enabled todos continue to work
- Manual todo updates remain valid
- The sync only runs when tasks explicitly match file operations
- No changes required to existing project structures

---

## Working with Existing Projects

When the user wants to continue work on an existing project:

1. **List all projects:**
   ```bash
   ls projects/
   ```

2. **Read project metadata:**
   ```bash
   read projects/{project-name}/project.md
   ```

3. **Read existing todos:**
   ```bash
   read_todos projects/{project-name}/todos/project-todo.todos.json
   ```

4. **Continue work** in that project folder

## File Organization Rules

| File Type | Location |
|-----------|----------|
| Source code | `projects/{name}/src/` |
| Configuration files | `projects/{name}/` (root) |
| Todo lists | `projects/{name}/todos/` |
| Documentation | `projects/{name}/docs/` |
| Sandbox exports | `projects/{name}/sandbox-files/` |
| Tests | `projects/{name}/tests/` |

## Benefits of This Structure

1. **Isolation:** Each project is self-contained
2. **Continuity:** Can pause and resume projects easily
3. **Clarity:** No confusion about which files belong to which project
4. **Scalability:** Can manage multiple concurrent projects
5. **History:** Project metadata preserves context

## Example: Creating a Flask App

```
User: "Build me a Flask app with a landing page"

Agent Workflow:
1. ✅ Read project-organization skill (THIS SKILL)
2. ✅ Create projects/flask-landing-app/
3. ✅ Create project.md, todos/, src/, sandbox-files/
4. ✅ Create project-todo.todos.json
5. ✅ NOW read flask-app-builder skill
6. ✅ Build the Flask app in projects/flask-landing-app/src/
7. ✅ Store all outputs in the project folder
8. ✅ Run Todo Sync Protocol after each file creation
```

## Quick Reference Checklist

Before starting any project:

- [ ] Check if `projects/` folder exists
- [ ] Create project folder with descriptive name
- [ ] Create `README.md` file (MANDATORY)
- [ ] Create `project.md` metadata file
- [ ] Create `todos/` subfolder
- [ ] Create `src/` subfolder
- [ ] Create `sandbox-files/` subfolder
- [ ] Create initial todo list inside `todos/`
- [ ] Update `.project-registry.json`
- [ ] THEN proceed with project-specific skill
- [ ] Run track-todo sub-action after file/folder operations

---

## Summary

**This skill is the foundation for all project work.** Always execute this skill's workflow BEFORE any other project-building skill. This ensures clean organization, prevents file conflicts, and enables continuous work across multiple projects.

The **Todo Sync Protocol** enhances this foundation by automatically tracking task completion based on your file operations, keeping your project todos always in sync with actual progress.