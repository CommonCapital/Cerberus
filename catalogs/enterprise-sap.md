# Enterprise Security Assurance Plan (ESAP)

A representative, **vendor-neutral** Security Assurance Plan (SAP) of the kind large enterprises (Fortune-500 procurement and CISO offices) use to evaluate a Provider before and throughout a contract. It is the full methodology and control set — the grading rubric Cerberus's Phase 2 (Enterprise Compliance) applies — reproduced here with the originating company's name and contacts removed. "the Customer" denotes the evaluating enterprise; "the Provider" denotes the party being assessed.

It is based on recognized standards and frameworks (ISO/IEC 27001, ISO/IEC 27002, NIST, OWASP).

---

## 1. Purpose

This document constitutes the Security Assurance Plan (SAP). It describes the security requirements of the Customer and the measures that the Provider commits to establish to meet them. Its purpose is to:

- Establish the respective responsibilities between the Customer and the Provider in terms of security.
- Describe the security requirements (technical, organizational and procedural) that the Provider commits to meeting throughout the service.

The Provider fills out this document describing the security measures and means at its disposal to meet the Customer's requirements. Depending on the service, certain requirements may not be applicable. The Provider submits the first version of the SAP to the Customer for validation; the Customer reviews the responses and may request additional information or clarification. Once validated by both parties, the SAP is appended to the contractual documents and becomes applicable.

Based on the responses, a vendor evaluation is performed. All vendor evaluations are determined by the Customer and at the Customer's sole discretion. Completion of this form is required for consideration in the vendor process; it does not warranty or guarantee selection. Supporting documentation should accompany all responses.

## 2. How grading works (the response schema)

For **every requirement**, the Provider returns:

| Field | Meaning |
|---|---|
| **Compliance Response** | One of: **Compliant** / **Partially compliant** / **Non-compliant** (and **Not applicable** where a requirement does not apply to the service). |
| **Provider's Detailed Response** | Justification/evidence for the chosen answer. |
| **Action Plan** | Required when the answer is **Partially compliant** or **Non-compliant** — the actions that will bring the requirement into compliance. |
| **Time limit for compliance** | A deadline for each action in the Action Plan. |

**Corrective-action timelines.** In case of non-compliance, the Customer and Provider implement corrective actions. The standard time limit for implementation is **30 days**; extensions may be requested and are subject to the Customer's approval; the **maximum** time limit shall not exceed **180 days**. Issues of disagreement are escalated for decision and validation.

**Review triggers.** The SAP is reviewed when any of the following occurs: an evolution (a modification of configuration or the Provider's IS environment, a change in service scope, a regulatory change, a request for a contract amendment, etc.); a security incident; or the annual review of the document. Any exemption from a security policy follows the exemption procedure, and the Provider documents any deviation from the Customer's security policies within the service scope.

## 3. Responsibilities (RACI)

| Activity | Customer | Provider |
|---|:--:|:--:|
| Provision of security requirements | X | |
| Description of responses to requirements | | X |
| Updating the document | X | |
| Document validation | X | |
| Publication of the document | X | X |
| Application of the document | | X |

## 4. Governance — committees & indicators

**Security committees** are organized regularly between the Customer and the Provider — **strategic** committees at least once a year (either party may also convene one), and, if required, **operational** committees during implementation. Participants include, at minimum, the single points of contact (SPOCs) of both parties. Topics include: review of security indicators and reporting; review of the handling of incidents and events; monitoring the progress of the corrective-action plan; reporting of security audits and follow-up of action plans; and any security-related information impacting the service.

**Example indicators:** number of security incidents (detected and corrected); percentage of actions closed for each action plan; residual risk level.

---

## 5. Control catalog

Each control below is graded with the response schema in §2. The **Req** numbers follow the source document (the General section is always completed; Application and SaaS sections apply to the relevant service type).

### A. General Security Governance

**Req 1 — Organization of Security.** The Provider shall have an organizational structure and means dedicated to information systems security. The responsible people shall be named, and eventual changes shall be notified to the Customer in due time. The Provider's information-systems-security stakeholders' contact information shall be made available to the Customer upon request.

**Req 2 — Information Systems Security Policy.** The Provider shall have a security policy for the management of its information system and ensure: the respect of this policy by all the Provider's personnel as well as all other participants (subcontractors, maintenance agents, …); and the regular updating and review of the policy. The Security Policy shall be made available to the Customer upon request.

**Req 3 — Security Certification.** The Provider shall make available to the Customer all documents (certification, audit report, declaration of applicability, etc.) attesting to their security certifications (SOC 2 Type 2, ISO 27001, etc.) and their scopes. In the response, indicate any certifications held including: Comprehensive — AICPA SOC 2; ISO/IEC 27001; HITRUST CSF. Government-sponsored — United States FedRAMP; Singapore SS584; European Union GDPR. Cloud-security-focused — Cloud Security Alliance STAR; ISO/IEC 27017. Financial-transaction-focused — BSI Kitemark Secure Digital Transactions; PCI DSS. Privacy-focused — ISO/IEC 27018.

**Req 4 — Risk Management.** The Provider shall formalize an information-system risk-management process. It shall implement the measures necessary to manage the risks incurred during the service.

**Req 5 — Security of the Provider's sites.** The Provider shall deploy all physical security measures necessary to comply with the confidentiality and business-continuity commitments of the contract. The Provider's premises used to provide the service to the Customer shall be secure and access shall be limited to authorized persons. The Provider shall specify a list of all the Customer's data-storage locations (main hosting site, backup sites, etc.) and shall agree to inform the Customer in case of a change of location.

**Req 6 — Confidentiality clause.** The Provider agrees to put in place the necessary means to ensure that all employees working on the Customer scope understand and respect their duties in terms of information-systems security. As a minimum, the Provider shall put in place: a non-disclosure agreement (NDA) applicable to the Customer data processed during the service and after the end of the employment contract; and a charter for the use of the information system.

**Req 7 — Vulnerability and Patch Management.** Deploy automated software-update tools to ensure that operating systems are kept current. The operating systems used by the Provider shall be hardened following state-of-the-art guidelines. The Provider shall perform regular vulnerability scans and correct any discovered vulnerability.

**Req 8 — Change Management.** The Provider shall have a change-management process for its entire information system to ensure: the security of technical and application updates; and service continuity.

**Req 9 — Security Incident Management.** The Provider shall ensure that there are written incident-response plans that define roles of personnel as well as phases of incident handling/management. If required, the Provider shall cooperate in investigations conducted by the Customer and provide the requested information (e.g. log files), in particular: provide regular reports on detected security incidents, time to resolution, and open incidents; notify the Customer of a security incident affecting its service delivery as soon as possible; allow the Customer to have a forensics investigation performed into Provider systems if the Customer data is concerned; and inform the Customer if a major security incident has occurred in its system in the last 12 months and of any future incident. In case of a security incident affecting personal data (data leak, data breach) the Provider shall directly notify the Customer's CISO within 24 hours.

**Req 10 — Liability insurance.** The Provider shall be insured against eventual cyber-related damages to the Customer. The insurance provider should be listed in the response.

**Req 11 — Business Continuity.** The Provider shall put in place all necessary measures to ensure the availability and continuity of services provided to the Customer. This includes: being compliant to required RTO and RPO; having a business-continuity plan and a disaster-recovery plan; and testing both regularly.

**Req 12 — Audits.** To assess the security level of the Provider, the Customer shall be able to conduct audits. The audits assess the Provider's maturity in terms of security and resilience; audit criteria are the security requirements in this SAP and the regulations the service is subject to. These audits: ensure this SAP's requirements are implemented; concern only the infrastructure and components that support the provided service; and can be organizational and/or technical (penetration testing, vulnerability scanning, code audit, infrastructure audit). The Provider shall collaborate and facilitate the audits and correct any discovered vulnerability (including third parties) on a schedule defined with the Customer. Where applicable, the Customer shall be able to access detailed reports of audits performed by the Provider or external auditors on the scope of the service over the past 12 months.

**Req 13 — Protection of personal data.** The Provider shall ensure compliance with the applicable personal-data-protection laws (e.g. USA: laws in over 20 states; Canada: PIPEDA; EU: GDPR; Australian Privacy Act; etc.).

### B. Application Security

**Req 14 — Secure Log-on Procedures.** Multi-factor authentication shall be deployed. If not compliant, refer to the next requirement.

**Req 15 — Password Management System.** Where possible, the application shall not manage passwords and shall leverage the business IDP to authenticate users through SSO. Where local authentication is used, passwords shall follow the guidelines set out by OWASP and should be stored in a Key Vault or similar security device. The Provider shall indicate how it can be compatible with the Customer's authentication solution (compatibility with the SAML v2 protocol) and with Azure AD / Entra ID.

**Req 16 — Encryption.** The Provider shall: support end-to-end encryption; offer unique encryption keys per customer (single tenancy); have documentation on their key-management procedures; allow the use of customer-managed encryption keys; allow pseudonymization or redaction when production data must be replicated to a sandbox environment; provide a mechanism for users to tag sensitive data or fields; and not use deprecated encryption algorithms.

**Req 17 — Encryption In-Transit.** The Provider shall use Transport Layer Security (TLS) version 1.2 or higher (with a preference for TLS 1.3). All deprecated encryption algorithms must be disabled.

**Req 18 — Separation of Development, Testing, and Operational Environments.** Development, testing, and operational environments shall be separated. The Provider shall continuously improve and evaluate the configuration of its own infrastructure. Administrators shall not be able to see the Customer data.

**Req 19 — Developer's awareness and training.** Developers need to be aware of the major cyber threats as described in OWASP. The OWASP Top 10 shall be applied and checked before delivery.

**Req 20 — Development Security.** The Provider shall have an operational development process. This process should include: acceptance tests prior to production; production data used only in production environments (anonymized where appropriate); and systematic code audits. Management of errors, exceptions, and unexpected behaviour in the application shall be provided for.

**Req 21 — Data territoriality.** The Provider shall comply with the laws of the selected region. Indicate the territory where the Customer data will be processed and stored; if "other", specify in the response.

### C. SaaS Security

**Req 23 — Pooling of infrastructures.** The Provider shall declare to the Customer all mutualized components that are part of the scope of the service. The Customer's data shall not be shared with the data of the Provider's other customers (logical and/or physical separation).

**Req 24 — Network Security Management.** The Provider shall implement detection and prevention components (IDS, IPS) on all incoming flows; implement at least two levels of filtering on all incoming flows; implement protection against denial-of-service (DoS) attacks to protect the service provided to the Customer; and set up a tool to detect leaks of sensitive data.

**Req 25 — Connection certificates.** The Provider shall utilize valid TLS certificates for external connections and internal protection of microservices (deprecated encryption algorithms are not allowed). The certificate should be from a well-known, trusted Certificate Authority.

**Req 26 — Information access restriction.** Information used in the SaaS application should only be accessible by authenticated users from approved devices that adhere to the business security policies of the SaaS consumer.

**Req 27 — Downloading.** Should information held in the SaaS application require downloading, appropriate security controls should be deployed, especially to avoid illegitimate data exfiltration.

**Req 28 — API-based access.** Any API-based access to any third party needs to be approved only after security checks (scan, review, consistency checks) and revoked once the business requirement is met.

**Req 29 — Provider access.** Should the Provider need to access the Customer data, the Customer's SPOC should be notified to evaluate the request and be given the opportunity to approve or deny it.

**Req 30 — Data restoration.** The Provider should regularly test data integrity on backup media by performing a data-restoration process to ensure the backups work properly. The backup needs to be aligned with the Customer's RPO/RTO requirements; if not otherwise stated, RPO and RTO are defined as 24 hours. Zero errors should be found during backup testing (regularly testing and verifying the backups so they are error-free and can be restored without issues).

**Req 31 — Information Backup.** The Provider shall have a comprehensive Backup Resilience Strategy: must follow a defined lifecycle that includes expiration policies; must be stored in an immutable (WORM) format or using other technologies that ensure an immutable copy (if the backup solution does not provide immutable backups, an offline/air-gapped solution shall be implemented); restrict access to backup systems and data to authorized personnel with a need-to-know, based on least privilege; implement protection mechanisms to avoid unwanted storage (physical/logical) deletion; implement protection mechanisms to avoid unwanted backup deletion; and implement the 3-2-1 backup rule — at least three copies (the primary plus two additional), at least two different media types or technologies (disc, tape, cloud, …), and at least one copy in a geographically diverse location.

**Req 32 — Encryption At-Rest.** The actively stored data and all backups shall be encrypted while at rest. Use strong encryption algorithms, at least AES-256 (or equivalent), to protect data backups from unauthorized access, tampering, or disclosure during transit and storage. The encryption method for data-at-rest shall meet the current FIPS standard. The Provider should refer to NIST SP 800-57 Part 1 (Recommendation for Key Management) for encryption strength based on algorithm and key length.

**Req 33 — Controls Against Malware.** The Provider shall deploy security measures to protect the Customer's information system from intrusions or malicious actions originating from the Provider's information system through its tools or the interconnection network. The Provider shall implement a security policy to protect its resources from malicious code; in particular, apply an anti-viral policy on all components hosting the service, automatically updated. Specify the antivirus software used on workstations and servers and the frequency of their updates.

**Req 34 — Red Team and Penetration testing.** Application administrators and their security organizations shall conduct regular internal and external penetration tests and Red Team exercises over the services provided. These tests must include at least a full scope of blended attacks, such as wireless, client-based, and web-application attacks. At least one penetration test shall have been conducted in the last 12 months. A remediation plan should be made available to the Customer upon request. The Provider shall ensure vulnerabilities are tackled by the remediation plan; remediation of critical vulnerabilities shall be prioritized and completed without delay.

**Req 35 — Service Availability.** The responsibility for managing data backup, redundancy, and failover in the case of infrastructure- or application-layer failures falls on the Provider. The Provider owns the remediation of technical issues. An uptime of 99.99% is required.

**Req 36 — Vulnerability and Patch Management (infrastructure).** The infrastructure used to deliver the service shall run the most recent security updates. Security patches shall be installed without delay. The Provider shall provide, on demand, summary reports to the Customer regarding the quantity of outstanding vulnerabilities.

**Req 37 — Archiving Policy.** Data used within this service and no longer in use shall be archived and available. In general: implement specific access modalities to archived data; destroy obsolete archives in a secure manner with a specified procedure; and make them available to the Customer on request.

**Req 38 — Administration activities.** The Provider shall specify the additional security means dedicated to the management of administrator accounts (authorization, revocation, and review of accounts and authorizations). Within the Provider's own information system, it shall comply with all prerequisites for administrative actions on the Customer perimeter: specific logins for administrator accounts; multi-factor authentication (described in the response); robust passwords; only individual accounts; traceability of administrator access and actions; and account monitoring (account and access review). Administrators shall use a dedicated machine/environment for all administrative tasks or tasks requiring administrative access.

**Req 39 — Traceability.** The Provider shall ensure the collection of: all security events on the systems and platforms used to provide the service; access logs to the platform; all administration actions performed on the platform; transactions on user accounts and authorizations; and restoration requests with authentication of the initiators. In addition, the Provider shall: protect logging equipment and logged information to preserve data integrity; have both a nominal and a backup channel for synchronizing internal time sources to ensure the viability of collected logs; monitor any intervention on these logs by a person in charge and report it to the SPOC; specify the extent to which it can provide automatic access to the Customer's logs to feed the Customer's SOC; and keep the logs (at least 6 months) in accordance with applicable regulations and provide them to the Customer upon request.

**Req 40 — Reversibility clause.** At the end of the contract, or in the event of activation of the reversibility clause, the Provider agrees to: guarantee the necessary assistance to securely transfer the Customer's property; return to the Customer a complete copy of its data in a usable format (this restitution shall be recorded, dated, and signed by the parties); and, once the restitution is made, proceed with the anonymization of the recovered material and the secure deletion of the Customer's data and technical information related to the Customer's infrastructure. A report shall be delivered to the Customer certifying that the data has been correctly deleted.

---

*Source note: numbering follows the originating workbook (Req 22 in the source was a layout artifact, not a distinct control, so this catalog contains 39 distinct controls across General 1–13, Application 14–21, and SaaS 23–40). The machine-readable form used by the Compliance engine is [`enterprise-sap.json`](enterprise-sap.json).*
