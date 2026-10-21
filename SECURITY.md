# Security Policy

## Supported Versions

Security fixes are applied to the latest released version.

| Version | Supported |
| ------- | --------- |
| latest  | Yes       |
| < latest| No        |

## Reporting a Vulnerability

Please report vulnerabilities privately through
[GitHub's private vulnerability reporting](https://github.com/taanning/umbraco-content-audit/security/advisories/new)
rather than opening a public issue.

Include the affected version, an Umbraco version, and steps to reproduce.
You can expect an acknowledgement within a few days.

## Notes

- Audit execution is read-only against CMS content; definitions and
  preferences live in package-owned tables.
- Every audit runs as the current backoffice viewer, including shared audits.
- Filter input uses a validated filter language - no user-supplied SQL or
  JavaScript is executed.
