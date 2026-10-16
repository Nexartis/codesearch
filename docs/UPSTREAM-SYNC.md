# Upstream sync ledger

This repository is **Nexartis's supply-chain mirror** of
[`flupkede/codesearch`](https://github.com/flupkede/codesearch). The fleet
installs a signed, SHA-256-pinned binary from this fork (see
`nexartis-ozzydev/mcp/upstream-forks.json` → `Nexartis/codesearch`,
`pin_version`, `pin_source: mcp/install.sh`). This file records the fork's sync
state and policy so a future sync wave starts from evidence, not rediscovery.

## Policy

- **Landing path:** `release/*` (or `develop`) → `master`. `protect-master.yml`
  rejects a PR to `master` whose head is not `develop` or `release/*`.
- **Source of truth:** upstream `develop` is the integration line; upstream
  `master` is the release line. Upstream develops on `develop`, releases to
  `master`, then merges `master` back into `develop`.
- **Pin:** the installed binary version is `pin_version` in
  `nexartis-ozzydev/mcp/upstream-forks.json`; a catch-up that changes the
  running version must bump that pin (and re-publish the fork release asset).

## Sync state

| Ref | Head | Version | Note |
| :-- | :--- | :------ | :--- |
| `master` | `07f0466` | v1.4.11 + sync #1 | default branch |
| `release/v1.5.2` | `7b47a9b` | upstream `master` v1.5.2 + 2 fork fixes | PR #4 (blocked on gated master-base verb) |
| `release/v1.5.32` | (this branch) | upstream `develop` v1.5.32 + 2 fork fixes | catch-up (this PR) |

## Catch-up: upstream `develop` v1.5.3 → v1.5.32 (143 commits)

Ranked by risk to the fleet's memory plane (a broken index silently degrades
every agent that reads code). Evidence is the upstream commit SHA; each fix is
also summarised in `CHANGELOG.md` under its version.

### Tier 0 — security

| SHA | Change |
| :-- | :----- |
| `e61b2cd` | refresh `quinn-proto` to 0.11.19 (Aikido `AIKIDO-2026-404995`, GHSA-53rm-773f-4q8c) — `Cargo.lock` only |

### Tier 1 — index integrity / data-loss class

| SHA | Change |
| :-- | :----- |
| `f149091` | stop wiping index caches on every binary swap; harden the transient FTS redrive |
| `241e641` | persist file metadata per index batch, atomically |
| `120169b` | chunk-store records stamped with the builder version, wiped on mismatch |
| `3807739` | retry the transient FTS commit race; keep failed adds registered |
| `43d0cc4`, `6120429` | diff-review findings: symbol pairing, noise, FTS scope, atomic save, rebuild guard |
| `85e0a4e` | file-meta invalidation messages go through `warn_print`, not `println` |

### Tier 2 — MCP tool correctness, status honesty, C# helper robustness

| SHA | Change |
| :-- | :----- |
| `93090a3` | `find` → `find_impact` position round-trip (1-based chunk lines, alias prefix, loud NotFound) |
| `07cdddb`, `abb792c` | negotiate 2026-07-28 MCP sessions so results keep `resultType` |
| `b4614bd`, `2cb4976` | expose symbol-index status on info surfaces and `find_impact` |
| `0e87917`, `6648d1f`, `ed2ef81`, `5dc47bc`, `ea7020d`, `7ce51e5` | status never reports ready while a rebuild runs (owner-scoped override, warning-not-error, bounded restore retry) |
| `9388426`, `51bbadf`, `2b65bed` | owner-scoped TUI indexing status; resting repos render `ok`/`Idle` |
| `ca02f79` | keep the repo label on indexing while a rebuild is queued |
| `e1963a5` | TUI shows `idx` for indexed-but-unloaded repos |
| `83dff3a` | symbol-index warnings wrap and are clipboard-copyable |
| `d70c508` | demote MSBuild advisories + BuildHost shutdown chatter to INFO |
| `278394c` | CLI stops instead of building locally when the serve is already indexing |
| `1cef3b6` | mark the db dir hidden on Windows so Search indexer leaves it alone |
| `cc46d3f` | re-resolve the map symbol in its workspace compilation before `FindReferencesAsync` |
| `bfcae91` | normalize backslash project paths in `NeedsRestore` |
| `afb5d61` | dedupe workspace loads, INFO-level env noise, quit during restore wait |
| `d2cf10a` | kill in-flight scip-csharp helper children on serve shutdown |
| `ea441ba` | net4x target spellings no longer raise the TFM floor |
| `4574c23` | VS-MSBuild restore fallback when the CLI SDK set cannot target the repo |
| `51b937d` | guard restore-pending exit-code parity between Rust and the C# helper |

### Tier 3 — features

| SHA | Change |
| :-- | :----- |
| `235f593` | run C# source generators (Razor) before symbol collection |
| `6540690`, `731f067`, `47a7527`, `95bc4a9`, `9fddb7c`, `680922e`, `48d1763`, `3029ac6`, `47587c6` | #225: per-project restore fallback + EOL-framework skip; restore-caused noise demoted; gated commands refuse to index while restore is in flight; restore freshness honours central package management |
| `f91c08c` | persist file metadata per index batch (per-batch atomic save) |

### Tier 4 — chore / deps / CI

| SHA | Change |
| :-- | :----- |
| `ad980c1` | tree-sitter-php 0.24.2 → 0.25.1 |
| `bd87dff` | cargo minor-and-patch group (6 updates) |
| `14c677f`, `bed86c8` | actions/upload-artifact, actions/download-artifact bumps |
| `92d8876` | docs: document the `core.bare` self-heal for direct git work |

## Not yet in `develop` (follow-up candidates)

- `upstream/perf/csharp-refs-at-index` (`fa10be7`, 5 commits beyond `develop`):
  `perf(symbols): ingest precomputed C# references at rebuild`,
  `perf(scip-csharp): emit references in one pass (index 2.1)`,
  `perf(scip-csharp): log index stage timings` + 2 test commits. A **performance**
  change, not a correctness one — land separately once upstream merges it.

## Fork-local deltas (preserved through this sync)

| Commit | Change | Upstream equivalent |
| :----- | :----- | :------------------ |
| `237a73c` | attribute `group=` hits to their owning repo; add `packages.lock.json` as a restore input | `a0c00b1` covers group attribution differently; upstream has **no** `packages.lock.json` input |
| `7b47a9b` | classify the unknown-`project=` refusal as a structured `project_unresolved` envelope | none — fork-local (NEE-P-CONSISTENT) |

## Reproduce

```sh
git remote add upstream https://github.com/flupkede/codesearch.git
git fetch upstream --tags
git log --oneline upstream/develop ^<branch>          # what is missing
git merge upstream/develop                            # catch up
cargo check --all-targets && cargo test
```
