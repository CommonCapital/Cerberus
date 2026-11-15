# Vendor Security Assessment

A catalog of what a customer usually needs to know about a software or SaaS provider's security before signing and while the contract runs. It is written for Cerberus and is free to use under the project's licence; it reproduces no company's questionnaire. The machine-readable form is [`vendor-security.json`](vendor-security.json).

## How it is answered

- **Verdict** for each control: Compliant, Partially compliant, Non-compliant or Not applicable.
- **Evidence**: what supports the verdict — a document, a setting, a report, or findings from a Cerberus scan.
- **Action plan** for anything not met: what will be done, by whom, and by when.
- **Deadline**: 30 days by default, at most 90. Adjust both to the contract.

32 controls in 6 sections. 12 can be judged from technical evidence; 9 have a signal from the Cerberus scanner today (`scannerCategories`), so a scan alone settles them. The rest need the provider's answer.

Review the answers again when the service, its hosting or the contract changes, after a security incident, and at least once a year.

## Governance and people (GOV)

**GOV-01 — Security ownership.** A named person or team is accountable for security, has the authority to act on it, and reports on it to leadership at least once a year.

Evidence: organisation chart, role description, last security report to leadership.

**GOV-02 — Written security policy.** Security rules are written down, approved, known to staff, and reviewed at least yearly or after a significant change.

Evidence: policy set, approval record, review log.

**GOV-03 — Risk assessment.** Risks to the service are identified, rated and given an owner and a treatment, and the register is revisited at least yearly.

Evidence: risk register, treatment plans.

**GOV-04 — Staff screening and agreements.** People with access to customer data are checked as local law allows before they start, and are bound by confidentiality terms that outlive their contract.

Evidence: screening procedure, confidentiality clause.

**GOV-05 — Security awareness.** Everyone with access is trained in security when they join and at least yearly, and engineers also receive secure-development training.

Evidence: training records, course content.

**GOV-06 — Independent assurance.** The provider can show a current independent assessment (certification, audit report or penetration test) of the service in scope, and lets the customer verify findings that concern it.

Evidence: certificate or audit report, latest penetration test summary.

## Identity and access (IAM)

**IAM-01 — Least privilege.** Access is granted per role, limited to what the job needs, approved before it is given, and removed promptly when the need ends.

Evidence: access request records, role matrix, leaver process.

**IAM-02 — Multi-factor authentication.** Administrative access, remote access and access to production require a second factor. *(judged from technical evidence)*

Evidence: identity provider settings, MFA enrolment report.

**IAM-03 — Access reviews.** Accounts and their rights are reviewed at least every six months, and orphaned or excessive rights are removed.

Evidence: review evidence, removed accounts.

**IAM-04 — Privileged access.** Privileged accounts are personal, separate from everyday accounts, logged, and used only when needed.

Evidence: privileged account list, session logs.

**IAM-05 — Customer sign-in.** Customers can use single sign-on or strong passwords with MFA; credentials are stored only as salted, slow hashes; sign-in attempts are rate-limited. *(judged from technical evidence)*

Evidence: authentication design, password storage scheme.

## Data protection (DAT)

**DAT-01 — Encryption in transit.** All connections carrying customer data use current TLS (1.2 or later) with strong ciphers, inside the provider's network as well as over the internet. *(scanner: crypto)*

Evidence: TLS configuration, internal service encryption.

**DAT-02 — Encryption at rest.** Customer data, backups included, is encrypted at rest with current algorithms. *(scanner: crypto)*

Evidence: storage encryption settings, backup encryption.

**DAT-03 — Secrets and keys.** Keys, passwords and tokens are kept in a secrets store, never in source code or images, are rotated, and are reachable only by the services that need them. *(scanner: secret)*

Evidence: secrets-store configuration, rotation records, secret scanning results.

**DAT-04 — Data location and retention.** The provider states where customer data is stored and processed, keeps it no longer than agreed, and deletes it verifiably at the end of the contract.

Evidence: data map, retention schedule, deletion certificate.

**DAT-05 — Separation between customers.** One customer's data cannot be read or changed by another customer, and the separation is tested.

Evidence: tenancy design, isolation test results.

**DAT-06 — Personal data.** Personal data is processed only as instructed by the customer and as data-protection law requires; sub-processors are listed and agreed.

Evidence: data processing agreement, sub-processor list.

## Secure development (DEV)

**DEV-01 — Security in the development process.** Security requirements and threat modelling are part of design, and every change is reviewed by someone other than its author before release.

Evidence: development procedure, threat models, review records.

**DEV-02 — Handling untrusted input.** Input from users and other systems is validated and encoded; queries are parameterised; untrusted data is never deserialised into live objects. *(scanner: injection, deserialization)*

Evidence: secure coding standard, static analysis results.

**DEV-03 — Security testing.** Static analysis runs on every change, and dynamic testing or a penetration test covers the service at least yearly and after major changes. *(scanner: injection, crypto, secret)*

Evidence: pipeline configuration, test reports.

**DEV-04 — Third-party components.** Open-source and commercial components are inventoried, checked for known vulnerabilities on every build, and updated within agreed times. *(scanner: dependency)*

Evidence: software bill of materials, dependency scan results.

**DEV-05 — Environments.** Development, test and production are separate; production data is not used in other environments unless anonymised.

Evidence: environment diagram, test-data procedure.

## Operations (OPS)

**OPS-01 — Patching.** Operating systems, platforms and applications are patched within defined times by severity, critical issues within days. *(scanner: dependency)*

Evidence: patch policy, patch compliance report.

**OPS-02 — Secure configuration.** Systems are built from hardened baselines, defaults and unused services are removed, and drift from the baseline is detected. *(scanner: config)*

Evidence: hardening baseline, configuration scan results.

**OPS-03 — Vulnerability management.** Infrastructure and applications are scanned for vulnerabilities at least monthly, and findings are fixed within times set by severity. *(scanner: config, dependency)*

Evidence: scan reports, remediation tracking.

**OPS-04 — Logging and monitoring.** Security-relevant events are logged centrally, protected from change, kept for at least a year, and watched so that alerts reach a person. *(judged from technical evidence)*

Evidence: logging architecture, retention settings, alert runbooks.

**OPS-05 — Network protection.** Networks are segmented, exposure to the internet is limited to what the service needs, and administrative interfaces are not publicly reachable.

Evidence: network diagram, firewall rules.

**OPS-06 — Malware protection.** Systems and uploaded files are protected against malicious code appropriate to their platform.

Evidence: endpoint protection coverage, upload scanning.

## Incidents and resilience (RES)

**RES-01 — Incident response.** There is a tested incident-response plan, and the customer is told of an incident affecting its data or service without undue delay, within the time agreed in the contract.

Evidence: incident plan, exercise records, notification clause.

**RES-02 — Backups.** Data is backed up to a separate location, backups are protected from alteration, and restores are tested at least yearly.

Evidence: backup policy, restore test results.

**RES-03 — Continuity.** Recovery time and recovery point objectives are defined for the service, and the continuity plan that meets them is tested.

Evidence: RTO / RPO statement, continuity test report.

**RES-04 — Availability commitment.** Availability is stated as a commitment, measured, and reported to the customer.

Evidence: service-level agreement, availability reports.
