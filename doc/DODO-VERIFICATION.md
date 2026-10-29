# Dodo 0.1.0 verification

This is a concise release record, not a claim that every upstream preview feature
or every Windows configuration is stable. It will be finalized after desktop
acceptance and the published-download check.

## Provenance

- PaperTodo baseline: `411078b094f9a22c15ffb2722f0b1cc7d0af0537`, version `4.0.0-beta1`.
- wpf-notifyicon submodule: `59b35037d4fcb65728eb630b71648d7f5da6e2de`.
- Full history and recursive submodule checkout retained. No upstream update during development.
- The unmodified baseline built with .NET SDK 10.0.403 and started in an isolated profile.
- Its two existing unused-variable warnings in `WindowNative.cs` remain; no build errors.

An incomplete synthetic baseline fixture initially omitted the required `papers`
field. The resulting startup error exposed a second null-reference in deferred
prewarming. The fixture was corrected, and Dodo now guards a missing controller state.
No pre-existing PaperTodo user data was used.

## Automated and background checks

| Area | Evidence |
| :--- | :--- |
| Capsule counts | 57 assertions: actual editing/completion, preview mutation, undo/redo, delete/batch, persistence, zero/disabled count, plugin content, long/narrow titles and layout scale variants. |
| Markdown export | Five groups: Unicode/Windows filenames, plain-text task escaping and completion, empty content, real LMDB image references, moved exports, repeated exports, missing images, read-only files and an NTFS deny-write directory. |
| Persistence | 31 checks including existing fault injection and six full-backup/restore groups: images, settings, native plugin state, resources, malformed/path-traversal/incompatible archives, rollback, interrupted journals and late-write suppression. |
| Actual processes | `tools/dodo-smoke.py` starts an isolated hidden Dodo process and a real stdio MCP bridge, discovers 20 tools, writes/reads Chinese notes and to-dos, changes settings, exits and restarts. Image bytes/references and native plugin state survive. |
| Coexistence | Isolated Dodo and PaperTodo run together, use separate mutexes/pipes/profiles, save and exit independently. |
| No desktop interference | In the background smoke sample: zero on-screen test windows and zero foreground activations. Three raw visible-flag windows were verified to be existing off-screen prewarm surfaces. |
| Visual review | English/Chinese light/dark screenshots rendered from real WPF controls. No `Show`, `Activate`, native HWND, input or clipboard use. These are explicitly offscreen renders, not DWM desktop captures. |
| Release notes | Seven existing bilingual release-note tests passed. |

Commands are documented in the root README and `tools/Dodo.VisualReview/README.md`.
Temporary fixtures/logs stay outside version control. The smoke script refuses to
run when a Dodo/PaperTodo GUI singleton is already active.

One hidden-profile observation: ready in 1,281 ms, reopen in 656 ms, working set
130.35 MiB, and 0 ms process CPU growth over a 3-second idle interval. This is a
single local sample, not a performance guarantee or comparison benchmark.

## Release acceptance still pending

- Final real-desktop input, focus, drag/dock/hover, settings-dialog backup/restore,
  theme and window-size checks. The user requested that foreground work wait for
  explicit permission; these checks have not been represented as passed.
- Final commit's GitHub CI and native LMDB source rebuild.
- Download the actual published ZIP from GitHub, verify SHA256 and launch that
  downloaded package in a new directory, then verify state after restart.

Mixed-DPI/multi-monitor transitions and HDR require appropriate hardware and
remain unverified unless explicitly recorded after testing. Offscreen scale
variants are not evidence of mixed-monitor correctness. IME composition must be
distinguished from programmatic Unicode input.

## Scope of data transfer

Backup format v1 replaces a complete supported profile. It excludes plugin
programs, plugin-owned external stores, external linked files, Windows startup
registry entries, caches and previous restore-history. Restoring a profile does
not silently change the machine's startup registration. Archives are bounded to
1 GiB of uncompressed data and fewer than 10,000 entries. Originals and a durable
journal remain under `dodo-data/restore-history`; interrupted replacements roll
back before profile loading. No merge or cloud synchronization is implemented.
