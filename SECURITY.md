# Security

This self-hosted application has no built-in authentication or multi-user isolation. Run it on loopback or a trusted network. Anyone who can reach its pages may access private energy information and configuration actions. An authenticated reverse proxy is required for access from untrusted networks.

Keep the database and encryption key outside the web root and restrict their directory/files to the service account. The supplied systemd example uses a dedicated `octopus` account and `/var/lib/octopus`; adapt permissions to the host rather than making the data world-readable. The key protects saved credential values; it does not encrypt consumption, identifiers, session evidence or billing data in SQLite. Use host disk/volume encryption where protection against device loss or offline access is required. Losing the matching key prevents decryption of saved credentials. Back up both securely.

Do not post exploit details, keys or personal data in public issues. Before sharing diagnostics, remove account numbers, MPAN/MPRN values, meter serials, device identifiers, supplier response payloads, private hostnames/IPs and household screenshots or logs. Use the repository's private vulnerability-reporting feature if available. If it is unavailable, open a minimal issue requesting a private reporting channel without disclosing the vulnerability or sensitive details. No security-response deadline is guaranteed.

Dependency advisories should be checked on the version being distributed:

```sh
dotnet list tests/JoakimHomeDashboard.Tests package --vulnerable --include-transitive
```

Do not interpret a clean package report as a complete security audit. Test updates on an isolated copy and retain a recoverable backup before changing an existing installation.

