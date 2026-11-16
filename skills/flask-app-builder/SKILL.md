---
name: flask-app-builder
description: Build Flask web applications from scratch. Use this skill whenever the user mentions Flask, Python web apps, REST APIs, web servers, or wants to create any kind of web application using Python, even if they don't explicitly say "Flask." This skill guides you through project setup, routing, templates, forms, APIs, and sandbox deployment.
---

# Flask App Builder

A skill for building Flask web applications from simple prototypes to production-ready projects.

## When to Use This Skill

Use this skill when:
- User wants to build a web application with Python
- User mentions Flask, Flask routes, or Flask templates
- User needs a REST API or web server
- User wants to prototype a web app quickly
- User needs to deploy a Python web app in a sandbox

## Overview

Flask is a lightweight WSGI web framework for Python. It's perfect for:
- **Prototypes & MVPs**: Get a working app in minutes
- **REST APIs**: Clean, simple API design
- **Small to medium web apps**: Full-stack applications
- **Learning web development**: Understand the fundamentals

This skill will guide you through the entire process: planning, structure, implementation, and deployment.

---

## Step 1: Understand Requirements

Before writing any code, clarify what the user needs:

### Questions to Ask

1. **What type of application?**
   - Static website (HTML pages served by Flask)
   - REST API (JSON responses, no frontend)
   - Full web app (routes + templates + forms)
   - Admin dashboard

2. **What features are needed?**
   - User authentication?
   - Database integration?
   - Form handling?
   - File uploads?
   - API endpoints?

3. **Complexity level?**
   - Single-file prototype (quick demo)
   - Package-based project (recommended default)
   - Blueprint-based architecture (large/complex apps)

4. **Deployment context?**
   - Sandbox environment (for testing/prototyping)
   - Production server (Gunicorn, Docker)

### Default Assumptions

If the user doesn't specify, assume:
- Package-based project structure
- SQLite database (if database needed)
- Development server for sandbox deployment
- Basic error handling included

---

## Step 2: Choose Project Structure

### Option A: Single-File Prototype

Best for: Quick demos, learning, single-purpose tools

```
app.py          # Everything in one file
templates/      # HTML templates
  index.html
static/         # CSS, JS, images
  style.css
```

### Option B: Package-Based Project (Recommended)

Best for: Most web applications, maintainable code

```
flask_app/
├── app/
│   ├── __init__.py      # App factory (create_app)
│   ├── routes.py        # URL routes
│   ├── models.py        # Database models (if needed)
│   ├── forms.py         # Form classes (if needed)
│   ├── templates/
│   │   ├── base.html    # Base template
│   │   └── index.html   # Page templates
│   └── static/
│       ├── css/
│       │   └── style.css
│       └── js/
│           └── main.js
├── config.py            # Configuration classes
├── requirements.txt     # Dependencies
└── run.py               # Entry point
```

### Option C: Blueprint Architecture

Best for: Large applications, team projects, modular features

```
flask_app/
├── app/
│   ├── __init__.py      # App factory
│   ├── auth/            # Authentication blueprint
│   │   ├── __init__.py
│   │   ├── routes.py
│   │   └── templates/
│   ├── api/             # API blueprint
│   │   ├── __init__.py
│   │   └── routes.py
│   ├── main/            # Main blueprint
│   │   ├── __init__.py
│   │   └── routes.py
│   ├── templates/
│   │   └── base.html
│   └── static/
├── config.py
├── requirements.txt
└── run.py
```

---

## Step 3: Core Implementation

### App Factory Pattern

Always use the app factory pattern for flexibility:

```python
# app/__init__.py
from flask import Flask

def create_app(config_class='config.DevelopmentConfig'):
    app = Flask(__name__)
    app.config.from_object(config_class)
    
    # Register routes
    from app.routes import main
    app.register_blueprint(main)
    
    # Initialize extensions (if any)
    # db.init_app(app)
    
    return app
```

### Configuration

```python
# config.py
import os

class Config:
    SECRET_KEY = os.environ.get('SECRET_KEY', 'dev-key-for-development-only')
    
class DevelopmentConfig(Config):
    DEBUG = True
    
class ProductionConfig(Config):
    DEBUG = False

class TestingConfig(Config):
    TESTING = True
```

### Routes

```python
# app/routes.py
from flask import Blueprint, render_template, jsonify, request

main = Blueprint('main', __name__)

@main.route('/')
def index():
    return render_template('index.html')

@main.route('/api/data')
def get_data():
    return jsonify({'status': 'success', 'data': []})

@main.route('/api/submit', methods=['POST'])
def submit():
    data = request.get_json()
    # Process data
    return jsonify({'status': 'created'}), 201
```

### Entry Point

```python
# run.py
from app import create_app

app = create_app()

if __name__ == '__main__':
    app.run(host='0.0.0.0', port=8000, debug=True)
```

---

## Step 4: Templates & Static Files

### Base Template

```html
<!-- app/templates/base.html -->
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>{% block title %}Flask App{% endblock %}</title>
    <link rel="stylesheet" href="{{ url_for('static', filename='css/style.css') }}">
</head>
<body>
    <header>
        <nav>
            <a href="{{ url_for('main.index') }}">Home</a>
        </nav>
    </header>
    <main>
        {% block content %}{% endblock %}
    </main>
    <script src="{{ url_for('static', filename='js/main.js') }}"></script>
</body>
</html>
```

### Page Template

```html
<!-- app/templates/index.html -->
{% extends "base.html" %}

{% block title %}Home - Flask App{% endblock %}

{% block content %}
<h1>Welcome to Flask</h1>
<p>This is your Flask application.</p>
{% endblock %}
```

### Static Files

Use absolute paths for static assets in sandbox environments:

```html
<!-- For sandbox proxy compatibility -->
<link rel="stylesheet" href="/static/css/style.css">
<script src="/static/js/main.js"></script>
```

---

## Step 5: REST API Design

### API Routes with Proper Status Codes

```python
from flask import Blueprint, jsonify, request, abort

api = Blueprint('api', __name__, url_prefix='/api')

@api.route('/items', methods=['GET'])
def get_items():
    items = []  # Fetch from database
    return jsonify({'items': items})

@api.route('/items/<int:item_id>', methods=['GET'])
def get_item(item_id):
    item = None  # Fetch from database
    if not item:
        abort(404)
    return jsonify({'item': item})

@api.route('/items', methods=['POST'])
def create_item():
    data = request.get_json()
    if not data or 'name' not in data:
        return jsonify({'error': 'Name is required'}), 400
    # Create item
    return jsonify({'item': data}), 201

@api.route('/items/<int:item_id>', methods=['PUT'])
def update_item(item_id):
    data = request.get_json()
    # Update item
    return jsonify({'item': data})

@api.route('/items/<int:item_id>', methods=['DELETE'])
def delete_item(item_id):
    # Delete item
    return '', 204
```

### Status Code Reference

| Code | Meaning | When to Use |
|------|---------|-------------|
| 200 | OK | Successful GET, PUT |
| 201 | Created | Successful POST |
| 204 | No Content | Successful DELETE |
| 400 | Bad Request | Invalid input |
| 401 | Unauthorized | Authentication required |
| 403 | Forbidden | Not allowed |
| 404 | Not Found | Resource doesn't exist |
| 500 | Server Error | Unexpected error |

---

## Step 6: Form Handling

### Using Flask-WTF

```python
# app/forms.py
from flask_wtf import FlaskForm
from wtforms import StringField, TextAreaField, SubmitField
from wtforms.validators import DataRequired, Email, Length

class ContactForm(FlaskForm):
    name = StringField('Name', validators=[DataRequired(), Length(min=2, max=50)])
    email = StringField('Email', validators=[DataRequired(), Email()])
    message = TextAreaField('Message', validators=[DataRequired(), Length(min=10)])
    submit = SubmitField('Send')
```

### Form Route

```python
@main.route('/contact', methods=['GET', 'POST'])
def contact():
    form = ContactForm()
    if form.validate_on_submit():
        # Process form data
        name = form.name.data
        return render_template('success.html', name=name)
    return render_template('contact.html', form=form)
```

### Form Template

```html
<!-- app/templates/contact.html -->
{% extends "base.html" %}

{% block content %}
<h1>Contact Us</h1>
<form method="POST">
    {{ form.hidden_tag() }}
    <div>
        {{ form.name.label }}<br>
        {{ form.name(size=40) }}
    </div>
    <div>
        {{ form.email.label }}<br>
        {{ form.email(size=40) }}
    </div>
    <div>
        {{ form.message.label }}<br>
        {{ form.message(rows=5, cols=40) }}
    </div>
    {{ form.submit() }}
</form>
{% endblock %}
```

---

## Step 7: Error Handling

### Custom Error Pages

```python
# app/errors.py
from flask import render_template
from app import app

@app.errorhandler(404)
def not_found_error(error):
    return render_template('errors/404.html'), 404

@app.errorhandler(500)
def internal_error(error):
    return render_template('errors/500.html'), 500
```

### API Error Responses

```python
@api.errorhandler(400)
@api.errorhandler(404)
@api.errorhandler(500)
def api_error(error):
    return jsonify({
        'error': error.name,
        'code': error.code
    }), error.code
```

---

## Step 8: Sandbox Deployment

### Requirements File

```txt
# requirements.txt
Flask==3.0.0
python-dotenv==1.0.0
# Add as needed:
# Flask-SQLAlchemy==3.1.1
# Flask-WTF==1.2.1
# Flask-Login==0.6.3
```

### Sandbox-Specific Configuration

For sandbox deployment, use these settings:

```python
# Sandbox-compatible settings
app.run(
    host='0.0.0.0',  # Required for container networking
    port=8000,       # Standard port
    debug=True       # Enable for development
)
```

### Important Sandbox Notes

1. **Host Binding**: Always use `host='0.0.0.0'` to accept connections from outside the container
2. **Static Files**: Use absolute paths (`/static/...`) for assets to work with proxy
3. **Session Cookies**: Set `SESSION_COOKIE_SECURE=False` for HTTP sandbox
4. **Secret Key**: Use environment variable or generate dynamically

### Running in Sandbox

```bash
# Install dependencies
pip install -r requirements.txt

# Run the application
python run.py

# Or use Gunicorn for production-like setup
gunicorn -w 4 -b 0.0.0.0:8000 "app:create_app()"
```

---

## Step 9: Security Best Practices

### Always Include

1. **CSRF Protection**: Use Flask-WTF for all forms
2. **Secret Key**: Never hardcode, use environment variables
3. **Input Validation**: Validate all user input
4. **SQL Injection Prevention**: Use SQLAlchemy ORM, never raw SQL with string formatting
5. **Error Messages**: Don't expose sensitive information in errors

### Security Configuration

```python
class Config:
    SECRET_KEY = os.environ.get('SECRET_KEY') or os.urandom(32).hex()
    SESSION_COOKIE_HTTPONLY = True
    SESSION_COOKIE_SAMESITE = 'Lax'
    # For production:
    # SESSION_COOKIE_SECURE = True
```

---

## Quick Start Templates

### Minimal Flask App

For rapid prototyping:

```python
from flask import Flask, jsonify

app = Flask(__name__)

@app.route('/')
def home():
    return jsonify({'message': 'Hello, Flask!'})

if __name__ == '__main__':
    app.run(host='0.0.0.0', port=8000, debug=True)
```

### Full Project Command Sequence

When building a complete Flask app in a sandbox:

1. Create directory structure
2. Write `config.py` with configuration classes
3. Write `app/__init__.py` with app factory
4. Write `app/routes.py` with routes
5. Create `app/templates/base.html`
6. Create `app/templates/index.html`
7. Create `app/static/css/style.css`
8. Write `requirements.txt`
9. Write `run.py` entry point
10. Install dependencies: `pip install -r requirements.txt`
11. Run: `python run.py`

---

## Common Patterns Reference

### Redirect with Flash Message

```python
from flask import flash, redirect, url_for

@main.route('/action')
def action():
    flash('Action completed successfully!')
    return redirect(url_for('main.index'))
```

### JSON Request Handling

```python
@main.route('/api/process', methods=['POST'])
def process():
    data = request.get_json()
    if not data:
        return jsonify({'error': 'No data provided'}), 400
    # Process data
    return jsonify({'result': 'success'})
```

### URL Parameters

```python
@main.route('/user/<username>')
def user_profile(username):
    return render_template('profile.html', username=username)

@main.route('/post/<int:post_id>')
def show_post(post_id):
    # post_id is automatically converted to int
    return render_template('post.html', post_id=post_id)
```

### Query Parameters

```python
@main.route('/search')
def search():
    query = request.args.get('q', '')
    page = request.args.get('page', 1, type=int)
    return render_template('search.html', query=query, page=page)
```

---

## Checklist Before Deployment

- [ ] All routes working correctly
- [ ] Templates rendering properly
- [ ] Static files loading (check paths)
- [ ] Forms validating and submitting
- [ ] API endpoints returning correct responses
- [ ] Error pages configured
- [ ] Secret key set via environment variable
- [ ] Debug mode appropriate for environment
- [ ] Host set to `0.0.0.0` for sandbox
- [ ] Dependencies in requirements.txt

---

## References

For more advanced topics, see the bundled references:
- `references/database.md` - SQLAlchemy integration
- `references/authentication.md` - Flask-Login setup
- `references/testing.md` - pytest configuration

Good luck building your Flask application!