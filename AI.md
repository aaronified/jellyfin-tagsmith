# AI.md

## Declaration

**Most of the code in this repository was written by an AI, under strict human
supervision.**

That is stated plainly here because you are entitled to know it before you install a plugin
that runs unattended against your media library, and because finding it out later, by
accident, would be worse.

## What that means in practice

The AI writes. A human directs, reviews, and decides. Nothing lands because a model
proposed it — every change is read by a person before it is committed, and the person
retains every decision that matters: what gets built, what gets refused, what ships, and
what is reverted.

The division is roughly:

| | |
| --- | --- |
| **AI** | Implementation, test-writing, refactoring, documentation drafts, investigating server behaviour |
| **Human** | Direction, design decisions, review of every diff, what ships and when, final judgement on anything touching user data |

The AI is treated as a capable contributor whose work still requires review — not as an
oracle, and not as an autocomplete.

## Why the supervision is strict

This plugin runs inside someone else's media server, against a database of someone else's
data, on a schedule, unattended. The realistic failure mode is not a crash — it is a
nightly task quietly doing something destructive and nobody noticing for a fortnight.

AI-written code is particularly good at being plausible. It will produce a call to a
Jellyfin API that does not exist, or that exists and is ignored, and it will do so in
well-formatted code with a confident comment attached. Several of the sharper bugs in this
project's history were exactly that. The guardrails below exist because of them, not in
anticipation of them.

## The guardrails

These are enforced on every change, and they are the reason the declaration above is not
just a disclaimer:

- **The tests gate everything.** The suite runs after every change; nothing proceeds on a
  failure. Tests pin behaviour to the specific server rule it mirrors, so a rule that
  changes upstream fails a test rather than shipping silently.
- **Server behaviour is verified by compiling, never recalled from memory.** Much of
  `Collections/` is a deliberate transcription of Jellyfin internals, and each rule is
  annotated with the server type it was read from. Those comments are citations. See the
  *Verified API surface* section of [docs/collections.md](docs/collections.md).
- **A separate review pass before release**, specifically for correctness against the
  Jellyfin API and the tag lifecycle guarantees — the class of problem tests do not catch.
- **Ownership is by id, never by name.** Tagsmith only ever modifies or deletes libraries,
  collections and tags it created and recorded. A library that merely shares a name with
  one of ours is never touched.
- **Dry run means no writes at all** — no libraries, no collections, no files, no
  deletions, no artwork, no configuration.
- **Unverified claims are marked as unverified**, in code comments and in documentation,
  rather than smoothed into confident prose. See
  [JELLYFIN-12-MIGRATION.md](JELLYFIN-12-MIGRATION.md) for what that looks like when a
  question genuinely cannot be settled yet.

## If you find something wrong

Report it. Provenance is not an excuse, and "the AI wrote it" is not a defence — the
supervision is the point, and a bug that reached you is a supervision failure, not a model
failure. Bugs are owned by the humans who shipped them.

## For AI agents working in this repository

The working rules — build commands, layout, the non-negotiable constraints around user
data, generated datasets and hand-maintained artwork — are in [CLAUDE.md](CLAUDE.md).
Read it before making changes.
