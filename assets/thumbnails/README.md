# Collection artwork

A starter set of collection posters and library tiles for three of the six namespaces
Tagsmith can project: `origin`, `lang` and `year`. Nothing is bundled for `award`,
`nomination` or `list` yet — those collections show what Jellyfin generates for them, a
copy of the first film's poster, until you supply files. They are **not** shipped inside
the plugin — the installable stays small and carries no fonts or drawing libraries.
Download the set you want, or make your own.

| Path | Covers | Files |
| --- | --- | --- |
| `origin/` | Country flags | 7 |
| `lang/` | Languages, named in their own script | 31 |
| `year/` | Decades, 1920s to 2020s — a curated retro set | 11 |
| `origin.png`, `lang.png`, `year.png` | The libraries' own home-screen tiles | 3 |

Generated posters are 400x600 PNGs and the curated decade posters 1000x1500 — both
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

## Regenerating

```bash
npm --prefix scripts install
node scripts/generate-artwork.mjs
```

`scripts/generate-artwork.mjs` rebuilds the `origin/` and `lang/` folders and the three
library tiles from scratch, and verifies that each file came out at its intended size
with something actually drawn on it, and that the face assigned to each language actually
carries that language's characters. The `year/` posters are the one **curated** set — the
generator verifies they are present but never rewrites them, so replacing a decade poster
is just replacing the file. Add a country or language by editing the tables near the top
of the script — the filename is derived from the display name using the same slug rules
as the C# side, so it stays correct by construction. A language whose writing system is
not already in the `SCRIPTS` table needs one row there and one `@fontsource` dependency
in `scripts/package.json` as well.

The complex scripts rely on the shaper: conjuncts, reordered vowel signs and cursive
joining are all resolved by HarfBuzz inside Skia, which is what `@napi-rs/canvas` draws
through. If you swap the canvas for one that only maps codepoints to glyphs, Indic and
Perso-Arabic names will come out plausible-looking but wrong — detached matras, unformed
conjuncts, letters in isolated instead of joined forms — so re-check the output by eye
rather than trusting that it is non-blank.

## Licence and attribution

- **Flags** — [`flag-icons`](https://github.com/lipis/flag-icons) by Panayiotis Lipiridis,
  MIT licensed. Drawn flat and unmodified apart from scaling; the blurred backdrop on each
  poster is a copy of the same artwork. Flag designs themselves are national symbols and
  are not copyrightable in most jurisdictions, but the MIT notice covers the SVG set.
- **Type** — the Noto Sans family: Noto Sans, Noto Sans JP, Noto Sans SC, and Noto Sans
  Arabic, Bengali, Devanagari, Gujarati, Gurmukhi, Kannada, Malayalam, Ol Chiki, Oriya,
  Tamil and Telugu. All under the [SIL Open Font License 1.1](https://openfontlicense.org/),
  obtained at build time via the `@fontsource` packages. Only rendered output ships here;
  no font files are redistributed in this folder.

  Urdu and Kashmiri are set in Noto Sans Arabic (Naskh) rather than Noto Nastaliq Urdu.
  Nastaliq is the better face for either language on its own, but its steeply cascading
  baseline will not share a fixed hero centre with fourteen upright Noto Sans faces, and
  Sindhi is conventionally set in Naskh regardless.
- **The posters** — MIT, same as the rest of this repository.
