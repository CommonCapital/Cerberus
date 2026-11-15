---
name: playwright-python-skill
description: Complete browser automation with Playwright for Python. Auto-detects dev servers, writes clean test scripts to /tmp. Test pages, fill forms, take screenshots, check responsive design, validate UX, test login flows, check links, automate any browser task using Python. Use when user wants to test websites, automate browser interactions, validate web functionality, or perform any browser-based testing with Python.
docker_image: playwright-python:3.11
---

**IMPORTANT - Path Resolution:**
This skill can be installed in different locations (plugin system, manual installation, global, or project-specific). Before executing any commands, determine the skill directory based on where you loaded this SKILL.md file, and use that path in all commands below. Replace `$SKILL_DIR` with the actual discovered path.

Common installation paths:

- Plugin system: `~/.claude/plugins/marketplaces/playwright-python-skill/skills/playwright-python-skill`
- Manual global: `~/.claude/skills/playwright-python-skill`
- Project-specific: `<project>/.claude/skills/playwright-python-skill`

# Playwright Browser Automation for Python

General-purpose browser automation skill using Python and Playwright. I'll write custom Playwright Python code for any automation task you request and execute it via the universal executor.

---

## 🐳 Docker Image for Screenshot Tasks

**IMPORTANT:** When taking screenshots of web pages, use the following Docker image:

| Property | Value |
|----------|-------|
| **Image** | `playwright-python` |
| **Tag** | `3.11` |
| **Full Reference** | `playwright-python:3.11` |

### When to Use This Docker Image

Use the `playwright-python:3.11` Docker image whenever:
- Taking screenshots of web pages
- Running browser automation in isolated environments
- Performing headless browser testing
- Automating any Playwright-based tasks

### Docker Commands

```bash
# Pull the image
docker pull playwright-python:3.11

# Run a screenshot script
docker run --rm \
  -v /tmp:/tmp \
  playwright-python:3.11 \
  python /tmp/playwright_test_screenshot.py
```

---

**CRITICAL WORKFLOW - Follow these steps in order:**

1. **Auto-detect dev servers** - For localhost testing, ALWAYS run server detection FIRST:

   ```bash
   cd $SKILL_DIR && python -c "from lib.helpers import detect_dev_servers; import json; print(json.dumps(detect_dev_servers()))"
   ```

   - If **1 server found**: Use it automatically, inform user
   - If **multiple servers found**: Ask user which one to test
   - If **no servers found**: Ask for URL or offer to help start dev server

2. **Write scripts to /tmp** - NEVER write test files to skill directory; always use `/tmp/playwright_test_*.py`

3. **Use visible browser by default** - Always use `headless=False` unless user specifically requests headless mode

4. **Parameterize URLs** - Always make URLs configurable via environment variable or constant at top of script

## How It Works

1. You describe what you want to test/automate
2. I auto-detect running dev servers (or ask for URL if testing external site)
3. I write custom Playwright code in `/tmp/playwright_test_*.py` (won't clutter your project)
4. I execute it via: `cd $SKILL_DIR && python run.py /tmp/playwright_test_*.py`
5. Results displayed in real-time, browser window visible for debugging
6. Test files auto-cleaned from /tmp by your OS

## Setup (First Time)

```bash
cd $SKILL_DIR
pip install -r requirements.txt
playwright install chromium
```

This installs Playwright and Chromium browser. Only needed once.

## Execution Pattern

**Step 1: Detect dev servers (for localhost testing)**

```bash
cd $SKILL_DIR && python -c "from lib.helpers import detect_dev_servers; import json; print(json.dumps(detect_dev_servers()))"
```

**Step 2: Write test script to /tmp with URL parameter**

```python
# /tmp/playwright_test_page.py
from playwright.sync_api import sync_playwright

# Parameterized URL (detected or user-provided)
TARGET_URL = 'http://localhost:3001'  # <-- Auto-detected or from user

with sync_playwright() as p:
    browser = p.chromium.launch(headless=False)
    page = browser.new_page()
    
    page.goto(TARGET_URL)
    print(f'Page loaded: {page.title()}')
    
    page.screenshot(path='/tmp/screenshot.png', full_page=True)
    print('📸 Screenshot saved to /tmp/screenshot.png')
    
    browser.close()
```

**Step 3: Execute from skill directory**

```bash
cd $SKILL_DIR && python run.py /tmp/playwright_test_page.py
```

## Common Patterns

### Test a Page (Multiple Viewports)

```python
# /tmp/playwright_test_responsive.py
from playwright.sync_api import sync_playwright

TARGET_URL = 'http://localhost:3001'  # Auto-detected

with sync_playwright() as p:
    browser = p.chromium.launch(headless=False, slow_mo=100)
    page = browser.new_page()
    
    # Desktop test
    page.set_viewport_size({'width': 1920, 'height': 1080})
    page.goto(TARGET_URL)
    print(f'Desktop - Title: {page.title()}')
    page.screenshot(path='/tmp/desktop.png', full_page=True)
    
    # Mobile test
    page.set_viewport_size({'width': 375, 'height': 667})
    page.screenshot(path='/tmp/mobile.png', full_page=True)
    
    browser.close()
```

### Test Login Flow

```python
# /tmp/playwright_test_login.py
from playwright.sync_api import sync_playwright

TARGET_URL = 'http://localhost:3001'  # Auto-detected

with sync_playwright() as p:
    browser = p.chromium.launch(headless=False)
    page = browser.new_page()
    
    page.goto(f'{TARGET_URL}/login')
    
    page.fill('input[name="email"]', 'test@example.com')
    page.fill('input[name="password"]', 'password123')
    page.click('button[type="submit"]')
    
    # Wait for redirect
    page.wait_for_url('**/dashboard')
    print('✅ Login successful, redirected to dashboard')
    
    browser.close()
```

### Fill and Submit Form

```python
# /tmp/playwright_test_form.py
from playwright.sync_api import sync_playwright

TARGET_URL = 'http://localhost:3001'  # Auto-detected

with sync_playwright() as p:
    browser = p.chromium.launch(headless=False, slow_mo=50)
    page = browser.new_page()
    
    page.goto(f'{TARGET_URL}/contact')
    
    page.fill('input[name="name"]', 'John Doe')
    page.fill('input[name="email"]', 'john@example.com')
    page.fill('textarea[name="message"]', 'Test message')
    page.click('button[type="submit"]')
    
    # Verify submission
    page.wait_for_selector('.success-message')
    print('✅ Form submitted successfully')
    
    browser.close()
```

### Check for Broken Links

```python
# /tmp/playwright_test_links.py
from playwright.sync_api import sync_playwright

TARGET_URL = 'http://localhost:3000'

with sync_playwright() as p:
    browser = p.chromium.launch(headless=False)
    page = browser.new_page()
    
    page.goto(TARGET_URL)
    
    links = page.locator('a[href^="http"]').all()
    results = {'working': 0, 'broken': []}
    
    for link in links:
        href = link.get_attribute('href')
        try:
            response = page.request.head(href)
            if response.ok:
                results['working'] += 1
            else:
                results['broken'].append({'url': href, 'status': response.status})
        except Exception as e:
            results['broken'].append({'url': href, 'error': str(e)})
    
    print(f"✅ Working links: {results['working']}")
    print(f"❌ Broken links: {results['broken']}")
    
    browser.close()
```

### Take Screenshot with Error Handling

```python
# /tmp/playwright_test_screenshot.py
from playwright.sync_api import sync_playwright

TARGET_URL = 'http://localhost:3000'

with sync_playwright() as p:
    browser = p.chromium.launch(headless=False)
    page = browser.new_page()
    
    try:
        page.goto(TARGET_URL, wait_until='networkidle', timeout=10000)
        
        page.screenshot(path='/tmp/screenshot.png', full_page=True)
        print('📸 Screenshot saved to /tmp/screenshot.png')
    except Exception as error:
        print(f'❌ Error: {error}')
    finally:
        browser.close()
```

### Test Responsive Design

```python
# /tmp/playwright_test_responsive_full.py
from playwright.sync_api import sync_playwright

TARGET_URL = 'http://localhost:3001'  # Auto-detected

viewports = [
    {'name': 'Desktop', 'width': 1920, 'height': 1080},
    {'name': 'Tablet', 'width': 768, 'height': 1024},
    {'name': 'Mobile', 'width': 375, 'height': 667},
]

with sync_playwright() as p:
    browser = p.chromium.launch(headless=False)
    page = browser.new_page()
    
    for viewport in viewports:
        print(f"Testing {viewport['name']} ({viewport['width']}x{viewport['height']})")
        
        page.set_viewport_size({
            'width': viewport['width'],
            'height': viewport['height']
        })
        
        page.goto(TARGET_URL)
        page.wait_for_timeout(1000)
        
        page.screenshot(
            path=f"/tmp/{viewport['name'].lower()}.png",
            full_page=True
        )
    
    print('✅ All viewports tested')
    browser.close()
```

## Async Pattern (for concurrent operations)

```python
# /tmp/playwright_test_async.py
import asyncio
from playwright.async_api import async_playwright

async def main():
    TARGET_URL = 'http://localhost:3001'
    
    async with async_playwright() as p:
        browser = await p.chromium.launch(headless=False)
        page = await browser.new_page()
        
        await page.goto(TARGET_URL)
        print(f'Page loaded: await page.title()}')
        
        # Run multiple operations concurrently
        await asyncio.gather(
            page.screenshot(path='/tmp/async_screenshot.png'),
            page.pdf(path='/tmp/async_page.pdf')
        )
        
        await browser.close()

asyncio.run(main())
```

## Inline Execution (Simple Tasks)

For quick one-off tasks, you can execute code inline without creating files:

```bash
# Take a quick screenshot
cd $SKILL_DIR && python run.py -c "
from playwright.sync_api import sync_playwright
with sync_playwright() as p:
    browser = p.chromium.launch(headless=False)
    page = browser.new_page()
    page.goto('http://localhost:3001')
    page.screenshot(path='/tmp/quick_screenshot.png', full_page=True)
    print('Screenshot saved')
    browser.close()
"
```

**When to use inline vs files:**

- **Inline**: Quick one-off tasks (screenshot, check if element exists, get page title)
- **Files**: Complex tests, responsive design checks, anything user might want to re-run

## Available Helpers

The skill includes helper functions in `lib/helpers.py`:

| Function | Purpose |
|----------|---------|
| `detect_dev_servers()` | Find running dev servers on common ports |
| `wait_for_server(url, timeout)` | Wait for a server to become available |
| `safe_goto(page, url, options)` | Navigate with error handling and retries |
| `capture_artifacts(page, prefix)` | Take screenshot + HTML dump for debugging |

### Using Helpers in Scripts

```python
# /tmp/playwright_test_with_helpers.py
import sys
sys.path.insert(0, '$SKILL_DIR')  # Add skill dir to path

from playwright.sync_api import sync_playwright
from lib.helpers import safe_goto, capture_artifacts

with sync_playwright() as p:
    browser = p.chromium.launch(headless=False)
    page = browser.new_page()
    
    # Use safe navigation with retries
    safe_goto(page, 'http://localhost:3001', timeout=15000)
    
    # Capture debugging artifacts
    capture_artifacts(page, 'test_run')
    
    browser.close()
```

## Common Selectors

| Task | Python Selector |
|------|-----------------|
| Click button | `page.click('button')` |
| Click by text | `page.click('text=Submit')` |
| Fill input | `page.fill('input[name="email"]', 'value')` |
| Select dropdown | `page.select_option('select', 'value')` |
| Check checkbox | `page.check('input[type="checkbox"]')` |
| Wait for element | `page.wait_for_selector('.loaded')` |
| Get text | `page.locator('h1').text_content()` |
| Get attribute | `page.locator('a').get_attribute('href')` |

## Debugging Tips

1. **Use slow_mo** to see actions: `p.chromium.launch(headless=False, slow_mo=100)`
2. **Pause for inspection**: `page.pause()` opens Playwright Inspector
3. **Trace recording**: Add `browser.start_tracing(page, path='trace.json')` for detailed traces
4. **Console logs**: `page.on('console', lambda msg: print(msg.text))`
5. **Error screenshots**: Always capture on failure in try/except blocks

---

## 🛡️ Bot Detection Avoidance (Stealth Mode)

**CRITICAL**: Many websites detect automated browsers. Use stealth techniques when testing production sites or bypassing bot detection.

### Quick Stealth Setup

```python
from playwright.sync_api import sync_playwright
from playwright_stealth import stealth_sync

with sync_playwright() as p:
    browser = p.chromium.launch(
        headless=False,
        args=['--disable-blink-features=AutomationControlled']
    )
    page = browser.new_page()
    
    # Apply stealth patches to avoid detection
    stealth_sync(page)
    
    page.goto('https://bot.sannysoft.com/')
    page.screenshot(path='/tmp/stealth_test.png')
    browser.close()
```

### When to Use Stealth

| Scenario | Stealth Required? |
|----------|-------------------|
| Testing localhost dev servers | ❌ No |
| Testing staging environments | ⚠️ Maybe |
| Scraping public websites | ✅ Yes |
| Testing production sites with bot protection | ✅ Yes |
| Bypassing Cloudflare/reCAPTCHA | ✅ Yes |

### Stealth Requirements

```bash
cd $SKILL_DIR
pip install playwright-stealth fake-useragent
```

### Essential Stealth Launch Options

```python
browser = p.chromium.launch(
    headless=False,
    args=[
        '--disable-blink-features=AutomationControlled',  # Critical!
        '--disable-features=IsolateOrigins,site-per-process',
        '--no-sandbox',
        '--disable-infobars',
    ]
)
```

### Human Behavior Simulation

```python
import random
import time

def human_delay(min_ms=100, max_ms=500):
    """Add realistic random delays."""
    time.sleep(random.uniform(min_ms/1000, max_ms/1000))

def human_scroll(page, distance=500):
    """Scroll naturally with pauses."""
    for _ in range(distance // 150):
        page.mouse.wheel(0, random.randint(100, 200))
        time.sleep(random.uniform(0.1, 0.3))
```

### Detection Test Sites

Test your stealth implementation:
- https://bot.sannysoft.com/ - Comprehensive bot test
- https://arh.antoinevastel.com/bots/areyouheadless - Headless detection
- https://pixelscan.net/ - Browser fingerprinting

### Complete Stealth Guide

See `STEALTH.md` for comprehensive documentation including:
- WebGL & Canvas fingerprint spoofing
- Navigator overrides
- Proxy configuration
- CAPTCHA handling
- Human behavior simulation patterns
- Complete evasion checklist

## Full API Reference

See `API_REFERENCE.md` for complete Playwright Python API documentation.

## Best Practices

1. **Always close browsers** - Use context managers (`with` statements) or try/finally
2. **Use locators** - More reliable than selectors, auto-wait built in
3. **Handle errors gracefully** - Wrap navigation in try/except with screenshots
4. **Parameterize URLs** - Makes scripts reusable across environments
5. **Use meaningful timeouts** - Don't use arbitrary waits; use `wait_for_*` methods
6. **Test accessibility** - Use `page.accessibility.snapshot()` to verify ARIA labels