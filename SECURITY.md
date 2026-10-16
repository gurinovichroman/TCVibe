# Security policy

TCVibe moves and deletes files, keeps connection passwords, runs a local MCP server that lets
coding agents read files and, with your approval, change them, and updates itself from GitHub
Releases. A flaw in any of that can hurt the people who use it, so please report it privately first.

## Reporting a vulnerability

Use **[Report a vulnerability](https://github.com/gurinovichroman/TCVibe/security/advisories/new)**
(GitHub private vulnerability reporting). Please don't open a public issue or discussion.

Include:

- the TCVibe version (**Help → Report a bug** shows it in the form it opens; don't submit that form),
- what an attacker can do and what they need for it,
- steps to reproduce, or a proof of concept.

You will get an answer in the advisory. Once a fix is released, the advisory is published with credit
to you, unless you prefer otherwise.

## Supported versions

Only the latest release gets security fixes. Update from **Help → Check for updates**.

## What is in scope

- The MCP server and the agent layer: reaching files outside the folders you approved, writing
  without approval, or reaching the server from another machine or user.
- Stored credentials (`data\connections.json`, encrypted with Windows DPAPI for your account) and
  how they are used: leaking them, or sending them to a host other than the one you configured.
- Remote connections: accepting a changed SSH host key or an unexpected TLS certificate.
- Archives and remote listings that write outside the target folder (path traversal, "zip slip").
- The update channel: making the program install something that is not an official release.
- Command-line injection through file names, tool macros or the command line.

Not in scope: anything that needs an attacker who already controls your Windows account, or files
you open on purpose in an external program.
