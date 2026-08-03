# Tag lifecycle

How Tagsmith decides what a tag should be, and what it is allowed to change.

## Shape of a tag

```
<namespace><separator><value>       origin=united_states
```

The namespace and separator are configurable; the value is always normalised. Together the
namespace and separator form a **prefix** (`origin=`), and prefixes are the unit of
ownership — Tagsmith manages tags by prefix and ignores everything else.

A blank namespace or separator never becomes a prefix. The degenerate prefixes are
catastrophic — `""` would claim, and therefore delete, every tag in the library — so a
blank setting simply switches its namespace off rather than widening the claim.

Award, nomination and list values are **structured**: two segments joined by a colon,
each slugged on its own — `award=oscar:best_picture`, `list=imdb_top_250` (one segment).
The colon is part of the value, not a separator.

## The namespaces

| Namespace | Meaning | Source |
| --- | --- | --- |
| `origin=` | Production countries, all of them | TMDb (`production_countries` for movies, `origin_country` for series) or TVDb, falling back to Jellyfin's `ProductionLocations` |
| `lang=` | The **original language** — what the film or show was made in | TMDb/TVDb, falling back to the audio streams on the files |
| `audio_lang=` | Audio-track languages actually on the files (off by default) | Media streams; series sample their first 32 episodes by premiere date |
| `year=` | First release year | Jellyfin metadata |
| `award=` | Award wins, `ceremony:category` | Embedded dataset, by IMDb id (off by default) |
| `nomination=` | Nominations, winners included | Same dataset (off by default) |
| `list=` | Curated-list membership | Embedded dataset, by IMDb id (off by default; pick lists individually) |

External lookups reach TMDb through the server's built-in client and TVDb through the
official TheTVDB plugin when it is installed; a series matched only by a TVDb id is
bridged through TMDb's find endpoint when the plugin is absent. A **failed** lookup — a
rate limit, an outage — is not evidence about the item: the item keeps its existing
origin and language tags for that run instead of being rewritten from the fallback.

## Normalisation

`TagNormalizer.Slug` lowercases, strips diacritics, and collapses every run of
non-alphanumeric characters to a single underscore:

| Input | Slug |
| --- | --- |
| `India` | `india` |
| `Côte d'Ivoire` | `cote_d_ivoire` |
| `Bosnia & Herzegovina` | `bosnia_herzegovina` |
| `日本` | `日本` |

Non-Latin scripts survive intact — letters and digits in any script are kept.

## The pipeline

```
external lookup  ->  provider output  ->  country canonicalisation  ->  aliases  ->  prune and write
```

1. Each `ITagProvider` produces tags for the item — the core provider consults TMDb/TVDb
   first for origin and language, the award and list providers look the item's IMDb id up
   in their embedded datasets.
2. Country values pass through `CountryAliasCatalog` (origin namespace only). ISO codes —
   which is what TMDb and TVDb send — resolve outright: a code is unambiguous by
   definition, whatever some locale renders as the same letters.
3. `TagAliasMap` applies the shipped rewrite rules and then the user's, which can also drop
   tags. The user's rules are parsed last and so override a shipped rule for the same value.
4. `TagSynchronizer` removes every existing tag matching a managed prefix, adds the newly
   computed set, and writes only if the result differs from what is already there.

## Country canonicalisation

Every spelling of a country resolves to one canonical English name slug:

| Metadata says | Tag |
| --- | --- |
| `United States`, `United States of America`, `USA`, `US`, `Estados Unidos`, `États-Unis`, `美国` | `origin=united_states` |
| `Germany`, `Deutschland`, `Allemagne`, `DEU` | `origin=germany` |
| `Burma`, `Zaire` | `origin=myanmar`, `origin=congo_kinshasa` |
| `Czech Republic` | `origin=czechia` |
| `Palestinian Territory`, `Palestinian Territories`, `State of Palestine` | `origin=palestine` |
| `Democratic Republic of the Congo`, `DR Congo`, `DRC` | `origin=congo_kinshasa` |

The dictionary is generated from Unicode CLDR by `scripts/generate-countries.mjs` and
embedded as a gzipped resource. It covers every ISO 3166-1 territory across all 391 CLDR
locales, plus alpha-2 and alpha-3 codes and a curated list of official long names in
`scripts/country-aliases-extra.json`. Roughly 17,000 aliases in 128 KB.

Aliases that CLDR renders identically for two different countries are dropped rather than
guessed at, so an ambiguous value tags as itself instead of tagging as the wrong country.

**States that no longer exist are deliberately not mapped.** Soviet Union, Yugoslavia,
Czechoslovakia and West Germany are not ISO 3166-1 territories, so they pass through as
`origin=soviet_union` and so on. Folding a 1960s Soviet film into `origin=russia` would
lose information you probably want. Add an alias rule if you disagree.

A territory that was merely *renamed* is a different case and does fold: Zaire is the same
place as the Democratic Republic of the Congo, as Burma is the same place as Myanmar.

**Bare `Congo` is left to CLDR**, which reads it as Congo-Brazzaville. It is genuinely
ambiguous in English and guessing the other way would be no better; the unambiguous long
names for both Congos resolve correctly.

Turn the whole thing off with *Canonicalise country names* to tag the raw metadata
value. That setting covers countries only; the language rules below are always applied.

## Language names

The server's localisation tables answer with ISO 639-2's scholarly headings rather than the
names people use, so the name is reduced before it becomes a tag — otherwise Spanish tags
as `lang=spanish_castilian` and the Spanish collection matches no artwork file.

| Table says | Tag |
| --- | --- |
| `Spanish; Castilian` | `lang=spanish` |
| `Dutch; Flemish` | `lang=dutch` |
| `Romanian; Moldavian; Moldovan` | `lang=romanian` |
| `Chinese (Traditional)`, `Chinese (Simplified)` | `lang=chinese` |
| `Portuguese (Brazil)`, `Portuguese (Portugal)` | `lang=portuguese` |
| `Greek, Modern (1453-)` | `lang=greek` (see below) |

Three rules, each matching a convention of the source table: a semicolon separates synonyms
so the first is kept; parentheses hold a qualifier — a script, a region, a date range — so
they are dropped; and a single comma marks an inverted heading, so the halves swap rather
than truncate. That last one matters: truncating would look right on `Greek, Modern` and
would quietly merge `Ndebele, South` and `Ndebele, North`, which are different languages.

Across every name the server can actually return — `LoadCultures` skips any ISO 639-2 row
with no two-letter code, which is 302 of the 496 — the only names that end up shared are
the script, region and dialect variants that should be: Chinese, French, Norwegian,
Portuguese and Spanish. This deliberately folds dialects: one Spanish collection, not
Spanish and Castilian.

Greek is the single exception, handled by code rather than by the rules. `Greek, Modern`
de-inverts correctly to "Modern Greek", but `el` is the only Greek the table can return,
so the qualifier is noise and it is overridden to `lang=greek`.

`audio_lang=` values come from the same resolution and are rewritten the same way.


## Aliases

### The one that ships enabled

```
lang:urdu => hindi
audio_lang:urdu => hindi
```

Hindi and Urdu share a collection unless you say otherwise. They are separate languages on
paper — ISO 639-1 `hi` and `ur`, ISO 639-2 `hin` and `urd`, their own CLDR rows, Devanagari
against Perso-Arabic — but in an audio-visual medium that distinction is largely inaudible.
The dialogue either side of the border is spoken Hindustani; the script decides how the
subtitles are set rather than what the film sounds like. Shelving on it splits the same
actors, the same playback singers and often the same picture into two places, and neither
is where someone browsing would look.

To turn it off, override each rule with one that maps the value to itself. **Both lines are
needed** — two rules ship, and one line only overrides one of them:

```
lang:urdu => urdu
audio_lang:urdu => urdu
```

They must be namespace-scoped. A bare `urdu => urdu` will *not* work: `Apply` consults
scoped rules before global ones, so the shipped scoped rule wins over your global one
whatever order they are written in.

Expect the Urdu films to move into their own collection on the next sync, and
[`assets/thumbnails/lang/urdu.png`](../assets/thumbnails/lang/urdu.png) — which ships for
exactly this case — to start being used.

> **If you have turned *Remove obsolete tags* off**, the fold adds `lang=hindi` without
> removing the existing `lang=urdu`, and the film stays in both collections permanently —
> the next run sees nothing left to do. This is how that setting behaves for any changed
> value, not something specific to this rule, but this is the first default that triggers
> it without you doing anything. Turn pruning on for one run to clean up.

This is an alias rather than a fold in `LanguageCodes.Normalise` on purpose. Normalising
would make the merge unconditional and invisible; as an alias it is a default rather than a
law, and it shows up where every other rewrite rule does.

The rules are written against the *configured* namespaces, so renaming `lang` to `language`
carries the rule across. One limitation: `Parse` slugifies a rule's scope while `Apply`
matches on the raw namespace, so a namespace that does not slugify to itself — `orig-lang`,
`audio.lang`, anything with a separator or a space — silently misses. That affects any
hand-written scoped rule equally; it is not particular to the shipped ones.

### Your own

Free-form rewrite rules, applied after canonicalisation, one per line:

```
origin:united_states => usa      # scoped to one namespace
bengali => bangla                # applies in every namespace
origin:unknown =>                # empty replacement drops the tag
award:oscar:best_picture => oscar:bp   # structured values keep their colons
# comments and blank lines are ignored
```

Both sides are normalised, so `USA` is stored as `usa`. Values are normalised per
colon-separated segment, which is what lets a rule target the structured award and
nomination values — those rules must use the namespace-scoped form, since a bare
`oscar:… =>` line reads as a rule scoped to an `oscar` namespace. A scoped rule beats a
global rule for the same value. A malformed line is skipped rather than throwing — one
bad line in the settings page cannot break a library scan.

## Pruning, and why `KnownPrefixes` exists

On every run, tags matching a managed prefix are removed and replaced with the freshly
computed set. That is what makes a changed value a rewrite rather than a duplicate:

> `origin=united_states_of_america` becomes `origin=united_states` — the old tag matches
> the `origin=` prefix, so it is removed in the same pass that adds the new one.

The subtle case is when the *prefix itself* stops being active. Rename the namespace from
`origin` to `country`, switch the separator from `=` to `:`, or untick *Tag production
countries*, and every `origin=` tag would be orphaned — no longer produced, and no longer
recognised as Tagsmith's to clean up.

`KnownPrefixes` is the fix. Every prefix Tagsmith has ever written is recorded in
configuration and pruned on subsequent runs, whether or not it is still active. It is
visible and editable on the settings page: **delete a line to make Tagsmith forget those
tags and stop managing them**, which is how you keep a set of tags after turning a
namespace off.

A dry run does not add to it. Recording a prefix is a claim of ownership over every tag
carrying it, and a run whose whole purpose is to change nothing has no business making that
claim — otherwise an experiment you abandoned leaves Tagsmith deleting tags it was only
ever asked to describe. The log still names the prefixes the run pruned against.

## Ownership is by namespace

Tagsmith owns a **prefix**, not a list of individual tags. Everything under `origin=`
belongs to it regardless of who typed it; everything outside is untouched, permanently.

The consequence, which is deliberate: a tag you add by hand inside a managed namespace is
business as usual. It shows up in the collections projection like any other, it gets
rewritten when an alias changes the value globally, and it is removed when the generated
set for that item no longer contains it. If your metadata says Japan and you hand-add
`origin=india`, the next run replaces it.

To keep a tag Tagsmith will never touch, put it outside the managed prefixes — any plain
tag, or a namespace Tagsmith is not configured to use.

The alternative — recording per item which tags Tagsmith wrote and pruning only those —
was tried and rejected: it makes two kinds of tag that look identical but behave
differently, which is worse to reason about than one rule applied consistently.

## Guarantees

- A changed value rewrites its tag; it never adds a second one.
- Renaming a namespace, changing the separator, or disabling a namespace still cleans up.
- Tags outside the managed prefixes are never read, modified, or deleted.
- Nothing is deleted except tags carrying a prefix Tagsmith is using or has used before.
- Dry run writes nothing — no tags, no configuration (`KnownPrefixes` included), and
  nothing from the collections projection: no libraries, no collections, no artwork, no
  deletions. Changes go to the log only, prefixed `[dry-run]`.
- Re-running with unchanged settings is a no-op; the item is only saved when the computed
  tag set actually differs.

## Finding tagged items

Jellyfin removed tags from global search in 10.10 for performance reasons, so the search
box will not find `origin=india`. Tags are still filterable:

- Click any tag on an item's detail page to see everything sharing it.
- Direct URL: `/web/#/list.html?type=tag&tag=origin%3Dindia`
- API: `GET /Items?Recursive=true&IncludeItemTypes=Movie&Tags=origin%3Dindia`
- Most clients expose Filters → Tags in library views.

There is no prefix or wildcard matching — `origin=*` is not a thing. See `docs/plan.md`
for the planned search helper.
