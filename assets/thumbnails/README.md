# Collection artwork

Collection posters and library tiles for three of the six namespaces Tagsmith can project:
`origin`, `lang` and `year`. Nothing is bundled for `award`, `nomination` or `list` yet —
those collections show what Jellyfin generates for them, a copy of the first film's poster,
until you supply files. They are **not** shipped inside the plugin — the installable stays
small and carries no fonts or drawing libraries. Download the set you want, or make your
own.

| Path | Covers | Files |
| --- | --- | --- |
| `origin/` | Countries, including three historical states | 206 |
| `lang/` | Languages, named in their own script | 83 |
| `year/` | Decades, 1920s to 2020s — a curated retro set | 11 |
| `origin.png`, `lang.png`, `year.png` | The libraries' own home-screen tiles | 3 |

This is the whole of it. A collection outside these three sets — an uncovered country, a
language nobody drew, every award and list category — shows Jellyfin's own default until
you drop a file in. There is no fallback tree of blocks any more: a set of solid colour
placeholders used to sit beside this one, and it was removed once `origin/` and `lang/`
held real artwork.

Every poster is a 1000x1500 PNG, Jellyfin's 2:3 poster aspect. The library tiles are
960x540 — the 16:9 shape of the home screen's "My Media" cards — and sit at the root of
the tree, named after the *namespace* rather than a tag value.

`lang/` covers 72 languages. Each poster carries a proverb in the language, its English
gloss set quietly underneath, and the language's own name across a black band at the foot —
the endonym in its own script, romanised at the left and named in English at the right:

> Afrikaans, Amharic, Arabic, Armenian, Azerbaijani, Basque, Bengali, Bhojpuri, Bulgarian,
> Burmese, Cantonese, Catalan, Chinese, Croatian, Czech, Danish, Dutch, English, Estonian,
> Filipino, Finnish, French, Georgian, German, Greek, Gujarati, Hausa, Hebrew, Hindi,
> Hungarian, Icelandic, Indonesian, Irish, Italian, Japanese, Javanese, Kannada, Kazakh,
> Khmer, Korean, Latvian, Lithuanian, Maithili, Malay, Malayalam, Marathi, Nepali, Nigerian
> Pidgin, Norwegian, Odia, Pashto, Persian, Polish, Portuguese, Punjabi, Romanian, Russian,
> Serbian, Sindhi, Sinhala, Spanish, Sundanese, Swahili, Swedish, Tamil, Telugu, Thai,
> Turkish, Ukrainian, Uzbek, Vietnamese, Yoruba

Arabic, Hebrew, Persian, Pashto, Sindhi and Urdu are set right to left.

That is 72 images under 83 filenames. The eleven extra stems are below, each byte-identical
to the poster it duplicates, because the name on the poster is not always the name Jellyfin
gives the collection — whichever one lands, it gets the same artwork:

| Language | Filenames | Why |
| --- | --- | --- |
| Bengali | `bengali`, `bangla` | Jellyfin may name either |
| Odia | `odia`, `oriya` | Jellyfin may name either |
| Punjabi | `punjabi`, `panjabi` | Jellyfin may name either |
| Pashto | `pushto`, `pashto` | the server's table heads that row *Pushto* |
| Khmer | `central_khmer`, `khmer` | the server's table heads that row *Central Khmer* |
| Filipino | `tagalog`, `filipino`, `fil` | the server names `tl` *Tagalog* |
| Bhojpuri | `bhojpuri`, `bho` | see below |
| Maithili | `maithili`, `mai` | see below |
| Nigerian Pidgin | `nigerian_pidgin`, `pcm` | see below |
| Urdu | `urdu` | a copy of `hindi` — see below |

`bho`, `mai`, `pcm` and `fil` are bare ISO codes rather than names. A language with no
two-letter code is skipped by the server's own `LoadCultures`, so `FindLanguageInfo` never
resolves it and the tag falls through to the code itself — `lang=bho`, not
`lang=bhojpuri`. Bhojpuri, Maithili and Nigerian Pidgin have no other stem that can match
today; Filipino also answers to `tagalog`, since `tl` does resolve.

`urdu.png` is a copy of `hindi.png`, which is drawn as a Hindi–Urdu poster: the proverb is
set twice, in Devanagari and in Perso-Arabic, and the band names both languages. It is idle
by default, because Tagsmith ships an alias rule folding Urdu into Hindi and no collection
is named `urdu` unless you override it. Override the rule and the poster starts being used —
see [docs/tagging.md](../../docs/tagging.md#the-one-that-ships-enabled).

Eight languages that the previous set covered have no poster here: Assamese, Bodo, Dogri,
Kashmiri, Konkani, Manipuri, Sanskrit and Santali. They were dropped with the set they
belonged to rather than left behind at a different size and in a different style.

## Installing

Copy the folders you want into Tagsmith's thumbnail directory, keeping the layout:

```
<config>/tagsmith/thumbnails/origin.png            the Origins library tile
<config>/tagsmith/thumbnails/origin/india.png      one collection's poster
<config>/tagsmith/thumbnails/lang/bengali.png
<config>/tagsmith/thumbnails/year/1950s.png
```

`<config>` is Jellyfin's data directory — `/config` in the official Docker image,
`%ProgramData%\Jellyfin\Server` on Windows. Tagsmith also accepts the `config/`
directory inside it (`%ProgramData%\Jellyfin\Server\config`) when only that one holds a
thumbnails folder, and logs which of the two it resolved.

On the next sync, Tagsmith applies a poster or tile wherever the collection or library
has no image, or where the image it already applied has changed on disk. It never
overwrites artwork you picked by hand — only the **Reapply collection artwork** button
does that.

## Naming

The filename stem is slugified and compared against the slugified tag value, so **case
and separators do not matter**. All of these resolve to the same collection:

```
united_states.png    United States.png    united-states.png    UNITED STATES.PNG
```

Accepted extensions: `.png`, `.jpg`, `.jpeg`, `.webp`, `.gif`.

The stems here are the canonical tag values, so a file drops in and matches without any
renaming. To cover a value that is not in the set, name the file after the tag value as
Tagsmith writes it — check the collection name in Jellyfin if you are unsure.

## Maintaining

Nothing here is generated. This set was previously rebuilt by
`scripts/generate-artwork.mjs`, which has been removed now that `origin/` and `lang/` hold
real artwork rather than machine-drawn flags and machine-set names — so **these files are
the only copy**. Adding, replacing or removing a poster means editing this tree directly,
and there is no step that will recreate one. Get the filename right: see
[Naming](#naming) above, because the stem is what decides whether Tagsmith ever loads the
file.

If you draw a replacement for one of the languages written in a complex script, check the
text by eye rather than trusting that it is non-blank. Conjuncts, reordered vowel signs
and cursive joining are resolved by the shaper, and tooling that only maps codepoints to
glyphs produces plausible-looking but wrong Indic and Perso-Arabic text — detached matras,
unformed conjuncts, letters in isolated instead of joined forms.

## Licence and attribution

- **The `origin/` posters** — supplied artwork, not drawn in this repository. Each carries a
  national motif and a small flag chip. **The provenance of that source material is not
  recorded yet, so this line still needs filling in.** The `flag-icons` (MIT) notice that
  used to sit here covered the seven generated flag posters this set replaced, and no longer
  describes anything in this folder.
- **The `lang/` posters** — likewise supplied artwork, not drawn in this repository, and
  **their provenance is not recorded yet either**. Both the proverbs and the faces they are
  set in came with the files. The Noto Sans notice that used to sit here covered the
  machine-rendered set this one replaced, where the repository chose the fonts, and no
  longer describes anything in this folder.
- **Type** — Noto Sans, under the
  [SIL Open Font License 1.1](https://openfontlicense.org/), covering the three library
  tiles. Only rendered output ships here; no font files are redistributed in this folder.
- **The `year/` posters and the three tiles** — MIT, same as the rest of this repository.
