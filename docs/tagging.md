# Tag lifecycle

How Tagsmith decides what a tag should be, and what it is allowed to change.

## Shape of a tag

```
<namespace><separator><value>       origin=united_states
```

The namespace and separator are configurable; the value is always normalised. Together the
namespace and separator form a **prefix** (`origin=`), and prefixes are the unit of
ownership — Tagsmith manages tags by prefix and ignores everything else.

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
provider output  ->  country canonicalisation  ->  user aliases  ->  prune and write
```

1. Each `ITagProvider` produces tags for the item.
2. Country values pass through `CountryAliasCatalog` (origin namespace only).
3. `TagAliasMap` applies the user's rewrite rules, which can also drop tags.
4. `TagSynchronizer` removes every existing tag matching a managed prefix, adds the newly
   computed set, and writes only if the result differs from what is already there.

## Country canonicalisation

Every spelling of a country resolves to one canonical English name slug:

| Metadata says | Tag |
| --- | --- |
| `United States`, `United States of America`, `USA`, `US`, `Estados Unidos`, `États-Unis`, `美国` | `origin=united_states` |
| `Germany`, `Deutschland`, `Allemagne`, `DEU` | `origin=germany` |
| `Burma` | `origin=myanmar` |
| `Czech Republic` | `origin=czechia` |

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

Turn the whole thing off with *Canonicalise country names* to tag the raw metadata value.

## User aliases

Free-form rewrite rules, applied after canonicalisation, one per line:

```
origin:united_states => usa      # scoped to one namespace
bengali => bangla                # applies in every namespace
origin:unknown =>                # empty replacement drops the tag
# comments and blank lines are ignored
```

Both sides are normalised, so `USA` is stored as `usa`. A scoped rule beats a global rule
for the same value. A malformed line is skipped rather than throwing — one bad line in the
settings page cannot break a library scan.

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
- Dry run writes nothing — changes go to the log only.
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
