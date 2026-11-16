# Security policy

Cerberus is a security tool, so a weakness in it can expose the systems it is pointed at. Please report one privately.

## Reporting a vulnerability

Use GitHub's private vulnerability reporting: on this repository, open **Security → Report a vulnerability**. Please do not open a public issue, pull request or discussion for it.

Include what you can of:

- what is affected (the .NET backend, the CLI, the TypeScript server, a container image, a catalog or a skill) and which commit or version;
- how to reproduce it, and what an attacker gains;
- whether it needs an account, a configured model, internet access for agents, or a particular setting.

We aim to acknowledge a report within five working days and to agree a fix and a disclosure date with you. Credit is given in the release notes unless you ask otherwise.

## Supported versions

Cerberus has no stable release yet. Fixes go to the `main` branch; please test against it.

## What is in scope

- The backend API, its authentication, authorization (per-user data, engagements, consent for consequential tools), audit log and job pipeline.
- The CLI, including the tools it runs on the operator's machine and their permission prompts.
- The agents' guards: egress control for web access and the browser, masking of secrets in what agents see, tool switches and limits.
- The Docker files and compose stacks as shipped.

Out of scope: vulnerabilities that Cerberus *finds* in other software (report those to that software's maintainers), findings that need an already-compromised host or administrator access to the server's configuration, and the third-party reference material under `security-skills/` (report upstream).

## Testing

Test only your own installation. Do not run Cerberus, or tests of it, against systems you are not authorized to test.
