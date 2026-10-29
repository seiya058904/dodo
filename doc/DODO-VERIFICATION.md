# Dodo 0.1.0 verification

This is a concise release record, not a claim that every upstream preview feature
or every Windows configuration is stable. The initial release explicitly retains
the desktop coverage limits below. The published-download result is recorded in
the [v0.1.0 Release notes](https://github.com/seiya058904/dodo/releases/tag/v0.1.0).

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
| Persistence | 32 checks including safe native-error reporting, existing fault injection and six full-backup/restore groups: images, settings, native plugin state, resources, malformed/path-traversal/incompatible archives, rollback, interrupted journals and late-write suppression. |
| Actual processes | `tools/dodo-smoke.py` starts an isolated hidden Dodo process and a real stdio MCP bridge, discovers 20 tools, writes/reads Chinese notes and to-dos, changes settings, exits and restarts. Image bytes/references and native plugin state survive. |
| Restore-to-process chain | `--write-restored-integration-fixture` creates a complete ZIP, validates it, restores into a new directory and compares every restored file with the source and manifest SHA256. The unchanged restored profile then passes the actual-process MCP/exit/restart checks: three papers, one LMDB image, application settings, Clock and Focus Timer state. |
| Coexistence | Isolated Dodo and PaperTodo run together, use separate mutexes/pipes/profiles, save and exit independently. |
| No desktop interference | In the background smoke sample: zero on-screen test windows and zero foreground activations. Three raw visible-flag windows were verified to be existing off-screen prewarm surfaces. |
| Visual review | English/Chinese light/dark screenshots rendered from real WPF controls. No `Show`, `Activate`, native HWND, input or clipboard use. These are explicitly offscreen renders, not DWM desktop captures. |
| Release notes | Eight bilingual release-note tests passed. |
| Package candidates | Both ZIP variants were extracted to new directories and passed the actual-process MCP/restart checks. The self-contained candidate ran with local .NET environment overrides removed. These are local candidates, not published-download acceptance. |

Commands are documented in the root README and `tools/Dodo.VisualReview/README.md`.
Temporary fixtures/logs stay outside version control. The smoke script refuses to
run when a Dodo/PaperTodo GUI singleton is already active.

One hidden-profile observation: ready in 1,281 ms, reopen in 656 ms, working set
130.35 MiB, and 0 ms process CPU growth over a 3-second idle interval. This is a
single local sample, not a performance guarantee or comparison benchmark.

Cloud MSVC source builds exposed upstream LMDB warning C4172: its Windows
`mdb_strerror` fallback returns a stack buffer. Dodo now calls that function only
for the documented negative LMDB codes that return static strings; C errno,
other Windows errors and unknown codes use safe managed messages. The original
error code is retained. Boundary and system-error checks pass. The vendored
source remains unchanged, so the compiler warning itself remains visible.

## Desktop acceptance and explicit limits

An isolated portable copy was run on Windows 11 at the local 175% display scale.
Visible checks covered image-note display, Chinese and English text entry, undo,
to-do creation/completion, light/dark readability and normal exit/restart retention.
The paper menu displayed one unfinished item for a two-item list with one completed
item. Actual note/to-do screenshots are included in the README; only surrounding
desktop pixels were cropped/masked, with no generated UI content.

The Markdown save dialog opened, but both official desktop tools failed to bind
its owned modal window. Settings and capsule surfaces were not exposed as
targetable windows. Consequently, settings/count-toggle interaction, search UI,
capsule drag/dock/hover, and completion of export/backup/restore through their
native dialogs remain **unverified by desktop automation**. They are not recorded
as passed. Their underlying counts, search data, export and storage behavior is
covered by the separate checks above. All test instances exited normally.

The complete CI checks and native source rebuild passed for the implementation;
the formal Release workflow repeats those gates for its exact source commit.
Published ZIPs are downloaded to new directories, checked against SHA256SUMS and
tested as actual processes before the final delivery report. This is separate
from local development-output validation.

Mixed-DPI/multi-monitor transitions and HDR require appropriate hardware and
remain unverified unless explicitly recorded after testing. Offscreen scale
variants are not evidence of mixed-monitor correctness. IME composition must be
distinguished from programmatic Unicode input.

## Scope of data transfer

Backup format v1 replaces a complete supported profile. It excludes plugin
programs, plugin-owned external stores, external linked files, Windows startup
registry entries, caches and previous restore-history. Restoring a profile does
not silently change the machine's startup registration. Archives are bounded to
1 GiB of uncompressed data and at most 10,000 archive entries. Originals and a durable
journal remain under `dodo-data/restore-history`; interrupted replacements roll
back before profile loading. No merge or cloud synchronization is implemented.
