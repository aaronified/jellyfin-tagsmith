# Collection artwork

A starter set of collection posters and library tiles for three of the six namespaces
Tagsmith can project: `origin`, `lang` and `year`. Nothing is bundled for `award`,
`nomination` or `list` yet — those collections show what Jellyfin generates for them, a
copy of the first film's poster, until you supply files. They are **not** shipped inside
the plugin — the installable stays small and carries no fonts or drawing libraries.
Download the set you want, or make your own.

| Path | Covers | Files |
| --- | --- | --- |
| `origin/` | Countries, including three historical states | 206 |
| `lang/` | Languages, named in their own script | 31 |
| `year/` | Decades, 1920s to 2020s — a curated retro set | 11 |
| `origin.png`, `lang.png`, `year.png` | The libraries' own home-screen tiles | 3 |

[`../placeholders/`](../placeholders/) holds the **complete** set for these three namespaces:
the curated posters as they stood when it was last refreshed, plus a solid colour block for
every remaining collection. Install that tree instead of this one if you would rather every
collection had *something*. Its `origin/` predates the 206-country set here, so take
countries from this tree rather than that one. See its README.

The language posters are 400x600 PNGs and the country and decade posters 1000x1500 — both
Jellyfin's 2:3 poster aspect. The library tiles are 960x540 — the 16:9 shape of the home
screen's "My Media" cards — and sit at the root of the tree, named after the *namespace*
rather than a tag value.

`lang/` covers 27 languages — English, French, Spanish, Chinese, Japanese, and all 22
languages of the Eighth Schedule to the Constitution of India:

> Assamese, Bengali, Bodo, Dogri, Gujarati, Hindi, Kannada, Kashmiri, Konkani, Maithili,
> Malayalam, Manipuri, Marathi, Nepali, Odia, Punjabi, Sanskrit, Santali, Sindhi, Tamil,
> Telugu, Urdu

Between them those use eleven writing systems: Latin, Han, Devanagari, Bengali–Assamese,
Gurmukhi, Gujarati, Odia, Tamil, Telugu, Kannada, Malayalam, Ol Chiki and Perso-Arabic.
Urdu, Kashmiri and Sindhi are set right to left.

Four languages ship under two filenames each, because Jellyfin may name them either way:
`odia`/`oriya`, `manipuri`/`meithei`, `bengali`/`bangla`, `punjabi`/`panjabi`. The pairs
are byte-identical, so whichever one matches, the collection gets the same poster.

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
`scripts/generate-artwork.mjs`, which has been removed now that `origin/` holds real
artwork rather than machine-drawn flags — so **these files are the only copy**. Adding,
replacing or removing a poster means editing this tree directly, and there is no step
that will recreate one. Get the filename right: see [Naming](#naming) below, because the
stem is what decides whether Tagsmith ever loads the file.

The `lang/` posters were machine-rendered while that script existed, with their text
shaped by HarfBuzz inside Skia. If you redraw one, mind that the complex scripts depend
on the shaper: conjuncts, reordered vowel signs and cursive joining are all resolved
there. Tooling that only maps codepoints to glyphs will produce plausible-looking but
wrong Indic and Perso-Arabic names — detached matras, unformed conjuncts, letters in
isolated instead of joined forms — so check the output by eye rather than trusting that
it is non-blank.

## Licence and attribution

- **The `origin/` posters** — supplied artwork, not drawn in this repository. Each carries a
  national motif and a small flag chip. **The provenance of that source material is not
  recorded yet, so this line still needs filling in.** The `flag-icons` (MIT) notice that
  used to sit here covered the seven generated flag posters this set replaced, and no longer
  describes anything in this folder.
- **Type** — the Noto Sans family: Noto Sans, Noto Sans JP, Noto Sans SC, and Noto Sans
  Arabic, Bengali, Devanagari, Gujarati, Gurmukhi, Kannada, Malayalam, Ol Chiki, Oriya,
  Tamil and Telugu. All under the [SIL Open Font License 1.1](https://openfontlicense.org/),
  and covering the `lang/` posters and the three library tiles. Only rendered output ships
  here; no font files are redistributed in this folder.

  Urdu and Kashmiri are set in Noto Sans Arabic (Naskh) rather than Noto Nastaliq Urdu.
  Nastaliq is the better face for either language on its own, but its steeply cascading
  baseline will not share a fixed hero centre with fourteen upright Noto Sans faces, and
  Sindhi is conventionally set in Naskh regardless.
- **The `lang/` and `year/` posters and the three tiles** — MIT, same as the rest of this
  repository.
