---
name: static-website-asset-paths
description: Ensure that all static assets (CSS, JavaScript, images) in sandbox-generated websites are referenced using RELATIVE paths (without leading slash). This is necessary for assets to correctly load through OpenSandbox proxy URLs. Use this skill whenever generating or modifying HTML files in a sandbox environment, creating static websites, or when asset loading issues are reported in sandbox-deployed sites.
---

# Static Website Asset Paths

This skill ensures that all static assets in sandbox-generated websites are referenced using **RELATIVE paths** (without leading slash), which is necessary for correct loading through OpenSandbox proxy URLs.

## Why This Matters

When a sandbox serves a website through its proxy URL (e.g., `http://127.0.0.1:59936/proxy/8000/`), absolute paths like `/static/style.css` will NOT resolve correctly because the browser interprets them from the server root, bypassing the proxy path.

**Example:**
- Proxy URL: `http://127.0.0.1:59936/proxy/8000/index.html`
- Absolute path `/static/style.css` resolves to: `http://127.0.0.1:59936/static/style.css` ❌ (WRONG - missing `/proxy/8000/`)
- Relative path `static/style.css` resolves to: `http://127.0.0.1:59936/proxy/8000/static/style.css` ✅ (CORRECT!)

---

## Instructions

### 1. Relative Paths Only (NO Leading Slash)

All asset references must use paths **WITHOUT** a leading `/`:

- `<link>` tags for stylesheets
- `<script>` tags for JavaScript
- `<img>` tags for images
- `<video>` and `<audio>` tags for media
- Any other resource references (fonts, icons, etc.)

**Correct:**
```html
<link rel="stylesheet" href="static/style.css">
<script src="static/app.js"></script>
<img src="static/images/logo.png" alt="Logo">
```

**Incorrect (will fail in proxy):**
```html
<link rel="stylesheet" href="/static/style.css">
<script src="/static/app.js"></script>
<img src="/static/images/logo.png" alt="Logo">
```

### 2. Preserve Folder Structure

Maintain the same folder hierarchy from the sandbox inside `/app`. Do not flatten directories.

**Example sandbox structure:**
```
/app
  index.html
  about.html
  contact.html
  /static
    style.css
    app.js
    /images
      logo.png
      hero.jpg
```

Asset references should mirror this structure using RELATIVE paths:
```html
<link rel="stylesheet" href="static/style.css">
<img src="static/images/logo.png">
```

### 3. Always Create Directories First

Before writing files to nested paths like `/static/images/`, ensure the directories exist:

```python
# Create directory structure before writing files
file_create_directories(sandbox_id, [
    {"path": "/app/static"},
    {"path": "/app/static/images"}
])
```

### 4. Proxy-Compatible URLs

When returning the public endpoint to the user, verify that all asset references will load correctly through the proxy URL.

**Example endpoint:** `http://127.0.0.1:59936/proxy/8000`

All assets must resolve when the browser requests:
- `http://127.0.0.1:59936/proxy/8000/static/style.css`
- `http://127.0.0.1:59936/proxy/8000/static/app.js`
- `http://127.0.0.1:59936/proxy/8000/static/images/logo.png`

### 5. Complete Example

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Home</title>
    <link rel="stylesheet" href="static/style.css">
    <link rel="icon" href="static/images/favicon.ico">
</head>
<body>
    <header>
        <img src="static/images/logo.png" alt="Logo">
        <nav>...</nav>
    </header>
    <main>
        <h1>Welcome</h1>
        <video src="static/videos/demo.mp4" controls></video>
    </main>
    <footer>
        <script src="static/app.js"></script>
    </footer>
</body>
</html>
```

---

## Common Mistakes to Avoid

| Mistake | Problem | Solution |
|---------|---------|----------|
| `href="/style.css"` | Absolute path bypasses proxy | Use `href="static/style.css"` |
| `src="/static/app.js"` | Leading slash causes 404 | Use `src="static/app.js"` |
| `href="../css/main.css"` | Parent directory reference | Use `href="static/css/main.css"` |
| Missing directory creation | File write fails | Create directories before files |

---

## Verification Checklist

Before returning a sandbox website to the user, verify:

- [ ] All `<link>` hrefs do NOT start with `/` (use relative paths)
- [ ] All `<script>` srcs do NOT start with `/` (use relative paths)
- [ ] All `<img>` srcs do NOT start with `/` (use relative paths)
- [ ] All media (`<video>`, `<audio>`) srcs do NOT start with `/` (use relative paths)
- [ ] Directory structure is preserved (not flattened)
- [ ] Directories were created before writing files