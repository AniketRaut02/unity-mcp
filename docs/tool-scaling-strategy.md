# Tool Scaling Strategy — Making 312 Tools Feel Like 10

**Status:** All 6 phases shipped (`1.26.0`) — see §10 for per-phase status. Decisions on
the open design questions are recorded under "Decisions" near the end.

## 1. The problem, in real numbers from this project

The tool catalog is complete: 312 tools (231 atomic C#, 81 composite Python) across 26
groups. The group system (`manage_tools` activate/deactivate, plus the new Tool Groups
window's disable) already solves the *worst* case — without it, an MCP client would see
all 312 tool definitions on every single turn. But two problems remain even with groups:

**1. Finding the right group still costs tokens and guesses.** Today, discovering "which
of 26 groups has the tool I need" means either reading `manage_tools list_groups`'s full
output (26 descriptions + up to 312 tool names) or guessing from memory. A real session
building a horror FPS level touches terrain, timeline, audio, lighting, weapons,
enemy_ai, gameplay, and more — that's most of the catalog, activated one uncertain guess
at a time.

**2. Individual tool descriptions are themselves bloated.** Measured directly from this
codebase's own source (`unity/com.unitymcp.bridge/Editor/Tools/*.cs` and
`python/unity_mcp_server/workflows.py`):

| Source | Count | Total description chars | Avg/tool | Est. tokens |
|---|---|---|---|---|
| Atomic tool descriptions | 231 | 48,955 | 212 chars | ~12,240 |
| Atomic tool *parameter* descriptions | 879 params | 55,545 | 63 chars | ~13,890 |
| Composite tool descriptions | ~80 | 27,537 | 344 chars | ~6,880 |

That's **~33,000 tokens of raw description text alone** across the catalog — before
counting JSON schema structure (property names, types, `required` arrays, braces), which
typically adds 30–50% more on top for well-documented APIs. The single worst offender,
`wire_unity_event`, has a 1,647-character (~410-token) description — more than triple the
already-high average — because engineering narrative (which spikes were run, which bugs
were found) was written directly into the description string instead of staying in code
comments or `CHANGELOG.md`. Every future tool call that has that group active pays for
that narrative, every time, even though the model never needed it to *use* the tool.

This isn't a hypothetical concern specific to this project. Anthropic's own published
numbers: **seven MCP servers can consume ~67,300 tokens of tool definitions — 33.7% of a
200K context window — before the user types anything**, at roughly 200–800 tokens per
well-documented tool. This project's own measured average (~53 description-only tokens
per atomic tool, likely 150–250 tokens once schema structure is included) sits squarely
in that same range. The industry response, shipped November 2025 – February 2026:
Anthropic's **Tool Search Tool** (a `defer_loading` mechanism plus a dedicated search
tool using either regex or BM25 natural-language matching, which Anthropic reports
preserves ~85% of context that would otherwise be spent on unused tool definitions) and
Anthropic's **code-execution-with-MCP pattern** (representing MCP tools as importable
code APIs so a model writes orchestration code in a sandbox instead of one round trip per
tool call, reported up to ~98% token reduction in production use).

Sources:
- [Anthropic: Tool search tool docs](https://platform.claude.com/docs/en/agents-and-tools/tool-use/tool-search-tool)
- [Anthropic: Advanced tool use (code execution with MCP)](https://www.anthropic.com/engineering/advanced-tool-use)
- [MCP spec: Tool annotations](https://blog.modelcontextprotocol.io/posts/2026-03-16-tool-annotations/)
- [Token Optimize: Cut MCP tool overhead](https://www.tokenoptimize.dev/guides/reduce-tool-overhead-mcp-tokens)

**Important caveat on applicability:** Anthropic's Tool Search Tool (`defer_loading`,
`tool_search_tool_bm25_20251119`) is a **Claude API / client-side** feature — it controls
how a client like Claude Code presents tools *it already received* to the underlying
model. This MCP server cannot enable it directly; that's the connecting client's choice.
What this project *can* do is implement the **same pattern** at the protocol level we do
control — which is exactly what this document proposes: make search-before-activate the
natural, cheap, encouraged path through our own `manage_tools` tool, so that whichever
client connects (Claude Code, Codex, or anything else), the token-expensive step (full
schema exposure) only ever happens for tools about to be used.

## 2. Goals, in priority order

1. **Token efficiency** — minimize tokens spent on tool definitions the model never
   calls, without hiding tools the model legitimately needs.
2. **Tool-use accuracy** — fewer wrong guesses, fewer "activated the wrong group" dead
   ends, fewer round trips to find the right tool.
3. **Security** — at least as strong as today's (destructive-confirm gate, path guard,
   rate limiter, audit log, instance lock, disabled groups), plus real new capability
   where the research surfaced a concrete gap.
4. **Ease of use** — for the human operator (the Unity Tool Groups window, sane
   defaults) and for the AI (predictable errors that guide it back to the right action).
5. **No regression in what already works.** Every atomic tool, composite tool, and the
   group/disable mechanism built across 18 batches stays exactly as capable as it is
   today. This is about *how tools are discovered and described*, not what they do.

## 3. What's already good — keep it, build on it

- The group system (`groups.py`, `manage_tools`) — the coarse-grained on/off switch.
- The Tool Groups window (`MCPToolGroupsWindow.cs` / `MCPToolGroupConfig.cs`) — the
  human-only disable mechanism and the Unity-writes/Python-reads config file pattern
  (`Library/MCP/tool_groups_config.json`), re-checked on a cheap mtime throttle. This
  document reuses that exact pattern for the new Read-Only Mode toggle (§7).
- `tool_manifest.json` — Python already exports composite tool metadata for Unity to
  read. This document reuses it as the data source the new search index is built from.
- The destructive-confirm gate, path guard (symlink-safe), rate limiter, instance lock,
  and audit log — all orthogonal to tool *discovery* and unaffected by anything here.
- `batch_execute` — already a lightweight analogue of the "code execution" pattern's
  actual benefit (fewer round trips for multi-step sequences) without its actual risk
  (arbitrary code execution). See §8 for why we don't go further than this.

## 4. The architecture: four tiers of disclosure

Everything below implements the same idea Anthropic's Tool Search Tool and this
project's own group system already gesture at: **don't pay for a tool definition until
the model is actually about to use it.** Four tiers, each strictly cheaper than the one
below it:

```
Tier 0 — Always paid for (every session, every turn)
  core group's 18 tools + manage_tools' own description
  (~1,500 tokens today; see §6 for the trim target)

Tier 1 — One cheap call when the model doesn't know where to look
  manage_tools(action="search", query="...")
  Returns up to `limit` tool names + group + one-line summary. No schemas.
  (~150-400 tokens for a typical result set)

Tier 2 — Paid only for groups about to be used
  manage_tools(action="activate", groups=["weapons","enemy_ai"])
  Full schemas for that group's tools now appear in list_tools().
  (Exactly what happens today — unchanged mechanism, just reached faster and more accurately)

Tier 3 — The actual tool call
  Unchanged.
```

The only *new* mechanism is Tier 1. Tiers 0, 2, and 3 already exist; this plan makes
Tier 1 real and makes Tier 0 cheaper (§6).

### 4.1 Tier 0 — make the free tier actually teach the workflow

Two changes, both verified feasible against the exact `mcp` package version this project
already depends on (`mcp==1.28.1` — confirmed by direct inspection, not assumed):

- **Set `instructions` on `create_initialization_options()`.** `InitializationOptions`
  (in `mcp.server.models`) has a real `instructions: str` field that flows into the
  `InitializeResult` sent once at session start — confirmed via
  `InitializationOptions.model_fields` on the installed package. This is a one-time,
  per-session cost (not repeated per turn the way tool descriptions are), making it the
  cheapest possible place to teach the model the intended workflow: *"26 tool groups
  exist; only `core` is active by default; call `manage_tools` with `action=\"search\"`
  before guessing which group to activate; deactivate a group when you're done with it
  to keep your own context lean."*
- **Inline the full 26-group catalog directly into `manage_tools`'s own description.**
  Since `manage_tools` is a `core` tool, its description is *always* paid for anyway —
  folding in a compact `group: one-liner` catalog (roughly 700–900 tokens for all 26,
  based on the current average description length) means the model can often pick the
  right group **with zero additional round trips**, for the common case where the task
  obviously maps to one group (e.g. "add a jump scare" → `timeline`/`audio`). Search
  (Tier 1) remains for the less obvious cases.

## 5. Tier 1 in detail: `manage_tools(action="search")`

### Why a new action on an existing tool, not a new top-level tool

Adding a 313th tool to solve "too many tools" would be self-defeating. `manage_tools`
already owns group lifecycle (`list_groups`/`activate`/`deactivate`/`reset`); `search` is
a fifth action on the same tool, keeping the *count* of always-visible tools unchanged.

### Index construction

Built once per process, lazily on first use, from data this project already assembles
elsewhere — no new data collection:

- Unity's atomic tools: name, group, description, parameter names + descriptions (from
  `bridge.list_tools()`, the same call `list_tools()` already makes).
- Composite tools: name, group, description (from `workflows.all_workflows()`).
- Group descriptions themselves, indexed as pseudo-documents keyed by group name, so a
  broad query like *"how do I set up a scare sequence"* can surface the `timeline` group
  even when no individual tool name contains those words.

### Scoring: BM25, not embeddings

Anthropic's own tool search ships two variants — a regex variant for exact/technical
matches and a **BM25 variant explicitly positioned as "simpler for teams without regex
knowledge"** for natural-language queries. This project should implement BM25 (the
well-known ranking function: term frequency, inverse document frequency, length
normalization via `k1`/`b` parameters) in pure Python, no dependency:

- The entire corpus is 312 short documents — trivial for an in-memory BM25 implementation
  (no index files, no persistence, rebuilt in well under a second on process start).
- **Deliberately no embeddings model.** An embeddings-based semantic search would need
  either a local model (a real dependency + real startup-time cost + real disk space for
  something that's supposed to be a fast, offline dev tool) or a remote API call (network
  dependency, latency, and a privacy/cost concern for a tool that currently makes zero
  outbound network calls except `manage_packages`, whose network use is inherent to what
  it does). At this corpus size, keyword/BM25 relevance on well-written, keyword-dense
  descriptions (which this catalog already has, per the `docs/tool-catalog.md` writing
  convention) should perform close to embeddings for the actual query patterns an
  AI agent uses ("terrain sculpting tools", "how to add a flickering light") — these are
  keyword-rich, not paraphrase-heavy queries.
- Weighted multi-field scoring: tool name (weight 3), group name (weight 2), description
  (weight 1), parameter names + descriptions (weight 0.5). A query matching the tool name
  directly should always outrank one that only matches a parameter description.
- Hybrid boost: if the query is an exact or near-exact substring of a tool name, boost
  that hit to the top regardless of BM25 score — covers the common case where the model
  already half-remembers a tool's name from an earlier turn or from training data.

### Request/response shape

```jsonc
// request
{ "action": "search", "query": "flickering light scare", "limit": 8 }

// response
{
  "results": [
    { "tool": "add_flicker_light", "group": "lighting", "active": false,
      "summary": "Attaches a scaffolded MCPFlickerLight to a Light for horror-style flicker." },
    { "tool": "create_scare_sequence", "group": "timeline", "active": false,
      "summary": "Choreographs a scripted scare: light + audio + anim + camera beats on a Timeline." }
  ],
  "hint": "Call manage_tools(action=\"activate\", groups=[...]) for the group(s) above before calling a listed tool directly."
}
```

Deliberately **no full parameter schema** in the response — that's what `activate` +
the next `list_tools()` refresh is for (Tier 2). Keeping search results to name + group +
one-line summary is what keeps Tier 1 cheap regardless of how often it's called.

### `activate`/`deactivate`: accept multiple groups per call

Minor, high-value change: `activate`/`deactivate` currently take one `group` string.
Extend to also accept a `groups: string[]` array (backward compatible — `group` keeps
working for a single one), so a search result spanning two or three groups can be
actioned in one round trip instead of one `manage_tools` call per group.

### A soft budget guard, not a hard cap

Before activating a group, `manage_tools` can cheaply estimate the token cost of the
resulting active set (using the same description-length data this document's numbers
came from) and, if activating would push the active set past a configurable soft budget
(e.g. ~8,000 tokens of description text — a threshold to tune empirically once §6's
trimming lands), return a warning in the response rather than silently ballooning context
— but still perform the activation. This is guidance, not a block: a legitimately large
session (building a full level touching a dozen systems) shouldn't be prevented from
doing its job, but the model should be told when it's about to spend a lot of context so
it can choose to deactivate something no-longer-needed first.

## 6. Trim the descriptions themselves — a style rule, enforced

The search mechanism reduces *how many* groups get activated unnecessarily; it does
nothing about each tool's description being needlessly long once its group *is* active.
Both matter, and the second is arguably the easier, more mechanical fix.

**New rule:** a tool's `[MCPTool(...)]`/`@workflow(...)` description string documents
*what it does and when to use it* — never the engineering history behind it (which spike
found which bug, which batch added it, what was tried and rejected). That context belongs
in `CHANGELOG.md` and in code comments near the implementation, which no model ever pays
tokens to read on a routine tool call. Target: **≤ ~40 words / ~250 characters per tool
description**, ≤ ~15 words per parameter description, as a guideline — not a hard wall for
every single tool (a few, like a multi-mode tool such as `wire_unity_event`, may
legitimately need a bit more to document real behavioral branches), but the current
average of 212 chars/description and 63 chars/param, with a 1,647-character outlier,
shows there's real, mechanical room to cut without losing information the model needs.

**Enforcement, not a one-time cleanup:** add `test_tool_description_budget.py` to the
Python test suite (reading Unity's tool list via the same mechanism `manage_tools` uses,
plus `workflows.all_workflows()`) asserting every tool's description and every
parameter's description stay under a hard ceiling (generous enough not to be a nuisance —
e.g. 100 words / 600 characters — but tight enough to catch a `wire_unity_event`-sized
regression before it ships). A soft warning-only report for anything over the *target*
(not the hard ceiling) keeps the guideline visible without blocking every future tool
that reasonably needs a sentence more. This turns "keep descriptions lean" from a norm
that erodes over 18+ future batches into something the test suite actually catches — the
same enforcement culture this project already applies to compile checks, invoke smoke
tests, and the full `FakeBridge` suite.

**Rollout:** this is a real, scoped pass across all 312 tools' description strings — not
something to do inside this planning document. Proposed as its own follow-up batch (see
§10), likely the single largest line-item here by tool count touched, even though each
individual edit is small and mechanical.

## 7. Leverage the MCP spec's own tool annotations (real spec feature, verified)

The MCP spec defines four standard tool annotations — `readOnlyHint`, `destructiveHint`,
`idempotentHint`, `openWorldHint` — which well-behaved clients use for risk-based
decisions (e.g. auto-approving a read-only call without a permission prompt, since per
spec an unannotated tool defaults to the *most pessimistic* posture: potentially
destructive, non-idempotent, open-world). This project's own `[MCPTool]` attribute
already tracks `Destructive` server-side but never surfaces it as a protocol-level
annotation in `list_tools()`'s `types.Tool(...)` construction — a real, currently-unused,
free win:

| MCP annotation | Source in this project | Work needed |
|---|---|---|
| `destructiveHint` | `[MCPTool(destructive: true)]` — already tracked | Wire into `server.py`'s `types.Tool(annotations=...)` construction. Zero new C# work. |
| `readOnlyHint` | Not tracked today | New `[MCPTool(readOnly: true)]` opt-in param; seed a first pass from naming convention (`get_*`/`list_*`/`read_*`/`capture_*`/`sample_*` are very likely read-only) and hand-verify each before shipping — never guess this one silently, a wrong `true` here would make a client skip a confirmation it should have asked for. |
| `idempotentHint` | Not tracked today | Same pattern as `readOnlyHint`, lower priority — fewer clients act on it today per the research. |
| `openWorldHint` | Not tracked today | Mostly `false` (everything targets the local Unity project); explicitly `true` for `manage_packages` (hits the real Unity package registry over the network) and worth considering for `build_player`/`import_asset` (touch the filesystem outside the project). |

This costs a small amount of schema JSON (annotations are compact structured fields, not
prose) in exchange for real client-side security/UX behavior this project gets "for
free" from any MCP client that already implements the spec's annotation handling.

## 8. What we deliberately do *not* do, and why

- **No arbitrary code execution tool** (the "code execution with MCP" pattern's more
  radical form — a sandbox where the model writes orchestration code that calls tool
  wrappers as functions). This project already made and documented this call: `run_csharp`
  was deliberately excluded in an earlier batch specifically because arbitrary code
  execution bypasses every existing safety mechanism (confirm gate, path guard, rate
  limiter) at once. `batch_execute` already captures the actual efficiency benefit of the
  pattern (fewer round trips for a known sequence of calls) without reopening that
  decision. If a future MCP client-side standard emerges for safely sandboxed
  orchestration *without* granting raw code execution against the Unity project, it's
  worth revisiting then — not something to build unilaterally into this server now.
- **No embeddings/semantic search dependency** — covered in §5; BM25 on a 312-document
  corpus of keyword-dense, human-written descriptions should get very close to embedding
  quality for the query patterns that matter here, at zero dependency/network/startup
  cost.
- **No silent auto-deactivation of idle groups.** An agent that used `weapons` five
  minutes ago having it silently vanish would be a surprising, hard-to-debug failure mode
  violating the principle of least surprise. Instead: the `instructions` field (§4.1)
  and the soft budget guard (§5) *nudge* proactive deactivation; nothing removes a group
  the AI didn't ask to remove.
- **No change to what "disabled" means or how it's enforced** — the Tool Groups window's
  disable mechanism (immediate, indistinguishable-from-nonexistent, `core`-exempt) is
  correct as built and orthogonal to discovery efficiency. Search must respect it: a
  disabled group's tools must never appear in search results either, for the same reason
  they don't appear in `list_tools()` or survive a direct `activate` call today.

## 9. A genuine new security capability: Read-Only Mode

The research surfaced one concrete gap worth closing: today's destructive-confirm gate
is per-call (`confirm: true` on one destructive tool invocation) — there's no blanket
"only allow read-only tools for this entire session" switch a human could flip before
handing an AI a Unity project to "just look around, don't touch anything."

**Proposed:** a new toggle in the Tool Groups window, "Read-Only Mode," stored the exact
same way `disabledGroups` already is — a new key in the same
`Library/MCP/tool_groups_config.json` Unity already writes and Python's `groups.py`
already re-reads on a throttled basis. When enabled: any tool call whose `readOnlyHint`
(§7) is not `true` is refused, enforced at the same two independent points already used
for disabled groups (`MCPToolRegistry.Invoke` in Unity, and `server.py`'s `call_tool()`
for composite tools) — reusing existing plumbing almost entirely, since it's the same
"check a config-derived boolean before dispatching" shape already built for disabled
groups. This directly depends on §7's `readOnlyHint` annotations existing and being
accurate first.

**Secondary, lower-priority hardening:** `manage_packages(action="add")` currently
accepts any package identifier the real UPM registry will resolve — including arbitrary
git URLs, which is a real (if narrow) supply-chain surface for a security-conscious team.
Optional: an allowlist (e.g. `com.unity.*`, `com.unitymcp.*`, plus explicit entries)
configurable through the same Unity-writes-config pattern, defaulting to permissive
(today's behavior) so this doesn't surprise anyone who doesn't opt in.

## 10. Rollout plan

Sized and sequenced the same way the original 18 tool-catalog batches were — each phase
independently shippable, verified with real spikes/tests before moving on, not a big-bang
rewrite:

1. **Tier 1 core mechanism** — **Done** (`1.21.0`). BM25 index (`tool_search.py`) +
   `manage_tools(action="search")` + `activate`/`deactivate` accepting `groups:
   string[]` + the soft activation-budget guard. Verified via `test_tool_search.py`
   (pure BM25-algorithm unit tests, including the real stemming bug found and fixed
   during implementation — a query using "flickering" didn't match a tool named with
   "flicker" until stemming was added) and `test_manage_tools_search.py` (integration
   tests against the fake Unity bridge: search finds atomic and composite tools by
   keyword, reports real active state, respects disabled groups, the budget guard
   warns without blocking). Full existing suite reverified green alongside it.
2. **Tier 0 cheap wins** — **Done** (`1.22.0`). `instructions` field on `Server(...)`
   (`server.py`) + inlined "Groups at a glance" catalog appended to `manage_tools`'s own
   description (`workflows._compact_group_catalog`). Verified two ways: (a) full existing
   test suite reverified green with both changes in place; (b) a direct check that
   `server.create_initialization_options().instructions` actually equals the instructions
   string and survives the real `mcp==1.28.1` `InitializationOptions` construction path
   (not just "the field exists on the dataclass") — confirming it really reaches a real
   `InitializeResult`, not merely that the package *supports* the field.
3. **Description-budget audit + enforcement test** — **Done** (`1.23.0`). Full-catalog
   audit found only one real offender: `wire_unity_event` at 1,647 chars, more than
   double the next largest (`simulate_input`, 737) — trimmed to ~720 chars by cutting
   engineering narrative while keeping the operative behavior facts. Every other tool
   (230 atomic, 81 composite) was already reasonably sized, so no mechanical catalog-wide
   trim was needed. `test_tool_description_budget.py` now parses every tool description
   directly from source and hard-fails past 900 chars, so a future regression is caught
   automatically instead of needing another manual audit.
4. **Tool annotations** — **Done** (`1.24.0`). `destructiveHint` wired straight from the
   existing `Destructive` attribute; `readOnlyHint` added as a new opt-in attribute param,
   hand-verified per tool (40 of 42 naming-convention candidates confirmed genuinely
   side-effect-free; `get_frame_debugger_info`/`capture_profiler_frames` excluded despite
   their names since both flip a global recording toggle); `openWorldHint` true only for
   `manage_packages` (the one tool touching the real network package registry). Verified
   via `test_tool_annotations.py` reading back a real `list_tools()` response over the
   fake-bridge harness and confirming the annotation JSON is present and correctly shaped.
5. **Read-Only Mode** — **Done** (`1.25.0`). New `readOnlyMode` key in the same
   `tool_groups_config.json` disabled-groups already uses; enforced at both
   `MCPToolRegistry.Invoke` (atomic) and `server.py`'s `call_tool()` (composite), with an
   explicit refusal message rather than disabled-groups' "unknown tool" disguise, since
   this isn't about hiding existence. Coarse-grained by design: `manage_tools` as a whole
   is non-read-only, so enabling this mode blocks it too, including its own
   `search`/`list_groups` actions -- the MCP spec's tool-level (not action-level)
   annotation model doesn't support finer granularity without a bespoke mechanism this
   phase didn't build. Verified via `test_read_only_mode.py`: off by default, a mutating
   composite tool call succeeds with it off, is refused by name with the mode on, and
   normal behavior returns once it's off again.
6. *(Optional, lower priority)* `manage_packages` source allowlist — **Done** (`1.26.0`).
   New `packageAllowlist` config key (exact IDs or `"prefix.*"` wildcards), empty by
   default (unrestricted, unchanged behavior), enforced in `ManagePackages`'s `add` case
   only, editable from the same Tool Groups window. Pure C#, no Python-visible surface,
   so verified by reading the implementation and the full existing Python suite staying
   green (nothing on the Python side changed).

## 11. Success metrics (how we'll know this worked)

- **Baseline (Tier 0) cost** drops or stays flat in absolute terms, but now includes a
  one-time `instructions` payload that eliminates most blind `list_groups` calls —
  measure by comparing tool-call traces before/after for a few representative real
  sessions (e.g. "build an enemy encounter," "set up a jump scare").
- **Average atomic tool description length** drops from today's measured 212 chars to
  under the ~250-char *target* (already close — the real win is capping the tail:
  `wire_unity_event`'s 1,647 chars and the handful of others over ~600 chars), enforced
  going forward by `test_tool_description_budget.py` never regressing.
- **Fewer wasted `activate` calls per session** — a "wasted" activation being one where
  the AI activates a group, doesn't call any of its tools, and later deactivates or
  leaves it active unused. Hard to measure precisely without call-tracing
  instrumentation this project doesn't have yet; a reasonable proxy is manual review of
  a handful of real session transcripts before/after `search` exists.
- **Zero regressions**: full existing Python suite (`tests/test_*.py`) and a full Unity
  compile + the existing `InvokeSmokeTestN` series all still pass unchanged, since none
  of this touches tool *behavior* — only discovery, descriptions, and one new opt-in
  security mode.

## Decisions (resolved before implementation)

The open questions above were reviewed with the project owner and resolved as follows —
recorded here so this document stays the single source of truth for how implementation
should proceed:

1. **Phase ordering: search first.** Phase 1 (§10) — the BM25 search mechanism plus
   `activate`/`deactivate` accepting `groups: string[]` — ships before the description
   trim. Bigger structural win first; the trim (§6) is lower-risk and can follow.
2. **`readOnlyHint` first pass: auto-seed + hand-verify.** Seed a first-draft `true` for
   tools matching `get_*`/`list_*`/`read_*`/`capture_*`/`sample_*` naming, then manually
   review and confirm (or correct) every single one before it ships — the heuristic is a
   starting draft to speed up the pass, never trusted silently, per §7's own caution that
   a wrong `true` here would make a client skip a confirmation it should have asked for.
3. **Read-Only Mode scope: conservative.** A composite tool is blocked under Read-Only
   Mode unless it's been explicitly reviewed and marked `readOnlyHint: true` — no
   inference from what it calls internally. This means Read-Only Mode will block more
   composite tools until the annotation pass (§7/§10 phase 4) explicitly covers them, but
   avoids a wrong inference silently allowing a mutating call through.
4. **Numeric defaults: as proposed, tune later.** BM25 `k1=1.5`, `b=0.75`, default search
   `limit=8`; soft activation-budget warning threshold ~8,000 description-tokens (§5).
   None of these are researched against real usage yet — revisit once there's real
   search/activation traffic to tune against, but don't block implementation on getting
   them exactly right up front.
