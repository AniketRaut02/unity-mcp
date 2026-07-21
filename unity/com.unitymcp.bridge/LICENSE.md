# License

**TODO:** No license has been chosen yet. This file is a placeholder — the
package.json references it, but it needs real content before publishing
anywhere (Asset Store, a public repo, or otherwise).

Common choices for a Unity Editor tool like this:
- **MIT** — permissive, widely used for Unity Editor tooling and MCP servers.
- **Apache 2.0** — permissive, adds an explicit patent grant.
- A **commercial/proprietary** license, if this is going on the Asset Store
  as a paid asset — Unity's own Asset Store EULA has specific requirements
  worth reading before choosing wording here.

Whichever is chosen, update:
- This file, with the actual license text.
- `package.json`'s `"license"` field (currently
  `"SEE LICENSE IN LICENSE.md"`, which is correct UPM convention as long as
  this file has real content).
- Any third-party notices needed — this package depends on
  `com.unity.nuget.newtonsoft-json` (MIT) and the Python side depends on the
  official `mcp` SDK package (MIT) — both permissive, but worth listing
  explicitly if the chosen license requires third-party attribution.
