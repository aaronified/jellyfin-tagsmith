// Builds the starter collection artwork set.
//
//   npm --prefix scripts install
//   node scripts/generate-artwork.mjs
//
// Output: assets/thumbnails/<namespace>/<value>.png — 400x600 posters (Jellyfin's poster
// aspect) for the `origin`, `lang` and `year` namespaces.
//
// Filenames are derived by running the canonical display name through the same slug
// rules as the C# side, so what lands on disk is exactly what Tagsmith will match a tag
// value against. Nothing here ships inside the plugin: the set is downloaded separately
// and dropped into <config>/tagsmith/thumbnails/, which keeps fonts and drawing
// libraries out of the installable.
//
// Flag artwork comes from the `flag-icons` package (MIT). The flags are drawn flat, as
// published — no waving, no drop shadow, no 3D.

import { mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createCanvas, GlobalFonts, loadImage } from '@napi-rs/canvas';
import sharp from 'sharp';

const here = dirname(fileURLToPath(import.meta.url));
const modules = process.env.ARTWORK_MODULES ?? join(here, 'node_modules');
const outputRoot = join(here, '..', 'assets', 'thumbnails');

/** Mirrors TagNormalizer.Slug in the C# side; the two must stay in step. */
function slug(value) {
  if (!value) return '';
  const stripped = value.trim().toLowerCase().normalize('NFD').replace(/\p{Mn}/gu, '');
  return stripped
    .replace(/[^\p{L}\p{N}]+/gu, '_')
    .replace(/^_+|_+$/g, '')
    .normalize('NFC');
}

// ---------------------------------------------------------------------------
// Canvas geometry. Every poster in every namespace uses these, which is what makes
// the three sets read as one set when they sit next to each other in a grid.
// ---------------------------------------------------------------------------

const POSTER_WIDTH = 400;
const POSTER_HEIGHT = 600;
const SIDE_MARGIN = 32;
const BOTTOM_MARGIN = 44;
const CONTENT_WIDTH = POSTER_WIDTH - SIDE_MARGIN * 2;

/** Baseline of the bold white name every poster carries along its bottom edge. */
const LABEL_BASELINE = POSTER_HEIGHT - BOTTOM_MARGIN;

/** Vertical centre of the "hero" — the flag, the native-script name, the decade. */
const HERO_CENTRE = 268;

/** Height of the flat flag band on an origin poster (4:3 across the full width). */
const FLAG_HEIGHT = Math.round((POSTER_WIDTH * 3) / 4);

/** Near-black the scrim fades to, and the fallback background for every poster. */
const INK = { r: 10, g: 13, b: 18 };

/** Where the bottom scrim starts. Above this the artwork is untouched. */
const SCRIM_TOP = 340;

// ---------------------------------------------------------------------------
// Scripts. Registered from the @fontsource packages rather than from whatever the build
// host happens to have installed, so the output is the same everywhere. Each writing
// system gets its own alias and callers name the one they want — no fallback chains,
// because a silently substituted font renders tofu that nobody notices until release.
//
// The Brahmic scripts and Perso-Arabic need real shaping — conjuncts, reordered vowel
// signs, cursive joining. @napi-rs/canvas is Skia, which shapes through HarfBuzz, so
// `fillText` is enough; the output was checked glyph by glyph rather than assumed. If
// this is ever ported to a canvas that only maps codepoints to glyphs, every one of
// these will come out subtly wrong rather than visibly broken, so check again.
//
// `rtl` drives ctx.direction. Bidi would order an all-Arabic run right to left anyway,
// but stating it keeps a name that mixes in a Latin word or a digit from flipping.
// ---------------------------------------------------------------------------

const SCRIPTS = {
  Latin: { file: '@fontsource/noto-sans/files/noto-sans-latin-700-normal.woff2' },
  Japanese: { file: '@fontsource/noto-sans-jp/files/noto-sans-jp-japanese-700-normal.woff2' },
  Chinese: { file: '@fontsource/noto-sans-sc/files/noto-sans-sc-chinese-simplified-700-normal.woff2' },
  Bengali: { file: '@fontsource/noto-sans-bengali/files/noto-sans-bengali-bengali-700-normal.woff2' },
  Devanagari: { file: '@fontsource/noto-sans-devanagari/files/noto-sans-devanagari-devanagari-700-normal.woff2' },
  Gujarati: { file: '@fontsource/noto-sans-gujarati/files/noto-sans-gujarati-gujarati-700-normal.woff2' },
  Gurmukhi: { file: '@fontsource/noto-sans-gurmukhi/files/noto-sans-gurmukhi-gurmukhi-700-normal.woff2' },
  Kannada: { file: '@fontsource/noto-sans-kannada/files/noto-sans-kannada-kannada-700-normal.woff2' },
  Malayalam: { file: '@fontsource/noto-sans-malayalam/files/noto-sans-malayalam-malayalam-700-normal.woff2' },
  // Unicode and the npm package both spell the script "Oriya"; the language is Odia.
  Odia: { file: '@fontsource/noto-sans-oriya/files/noto-sans-oriya-oriya-700-normal.woff2' },
  OlChiki: { file: '@fontsource/noto-sans-ol-chiki/files/noto-sans-ol-chiki-ol-chiki-700-normal.woff2' },
  Tamil: { file: '@fontsource/noto-sans-tamil/files/noto-sans-tamil-tamil-700-normal.woff2' },
  Telugu: { file: '@fontsource/noto-sans-telugu/files/noto-sans-telugu-telugu-700-normal.woff2' },
  // Naskh, not Nastaliq. Nastaliq is the better face for Urdu and Kashmiri on its own,
  // but its steep cascading baseline does not sit on a fixed hero centre next to
  // fourteen upright Noto Sans faces, and Sindhi is conventionally set in Naskh anyway.
  // Noto Sans Arabic covers all three, including the Sindhi ڌ ڏ ڄ ٺ and Kashmiri ٲ.
  PersoArabic: { file: '@fontsource/noto-sans-arabic/files/noto-sans-arabic-arabic-700-normal.woff2', rtl: true },
};

for (const [alias, script] of Object.entries(SCRIPTS)) {
  const path = join(modules, script.file);
  if (!existsSync(path)) throw new Error(`missing font: ${path} (run npm --prefix scripts install)`);
  if (!GlobalFonts.registerFromPath(path, alias)) throw new Error(`could not register font: ${path}`);
}

// ---------------------------------------------------------------------------
// The three namespaces.
// ---------------------------------------------------------------------------

// ISO 3166-1 alpha-2 codes pick the flag out of flag-icons. The name is the canonical
// English one the country catalog resolves to, which is also what the filename slugs to.
const COUNTRIES = [
  { name: 'Japan', code: 'jp' },
  { name: 'India', code: 'in' },
  { name: 'United States', code: 'us' },
  { name: 'United Kingdom', code: 'gb' },
  { name: 'France', code: 'fr' },
  { name: 'China', code: 'cn' },
  { name: 'Hong Kong', code: 'hk' },
];

// `native` is the language's own name for itself, which is the whole point of the card.
// `script` names an entry in SCRIPTS above and decides both the face and the direction.
// `accent` tints the background so the set is scannable without reading anything: one
// hue per script family, stepped in lightness where a family has several members, so
// the eight Devanagari cards read as a run and the three Perso-Arabic ones as another.
// `aliases` are extra filenames to emit with identical artwork, for languages Jellyfin
// may hand us under either of two English names.
//
// Alphabetical by English name. Adding a language is one row plus, if its writing system
// is new, one row in SCRIPTS and one dependency in package.json.
const LANGUAGES = [
  { name: 'Assamese', native: 'অসমীয়া', script: 'Bengali', accent: '#18593a' },
  { name: 'Bengali', native: 'বাংলা', script: 'Bengali', accent: '#1f6f54', aliases: ['Bangla'] },
  // The Eighth Schedule spells the language Bodo; बड़ो is the usual Devanagari form of
  // the endonym (Boro), which is also written बर' with an apostrophe for the final vowel.
  { name: 'Bodo', native: 'बड़ो', script: 'Devanagari', accent: '#5f321c' },
  { name: 'Chinese', native: '中文', script: 'Chinese', accent: '#a6392a' },
  { name: 'Dogri', native: 'डोगरी', script: 'Devanagari', accent: '#69371e' },
  { name: 'English', native: 'English', script: 'Latin', accent: '#2b4c8c' },
  { name: 'French', native: 'Français', script: 'Latin', accent: '#3c4fa4' },
  { name: 'Gujarati', native: 'ગુજરાતી', script: 'Gujarati', accent: '#792a61' },
  { name: 'Hindi', native: 'हिन्दी', script: 'Devanagari', accent: '#733c21' },
  { name: 'Japanese', native: '日本語', script: 'Japanese', accent: '#8c2f39' },
  { name: 'Kannada', native: 'ಕನ್ನಡ', script: 'Kannada', accent: '#3b2d80' },
  { name: 'Kashmiri', native: 'کٲشُر', script: 'PersoArabic', accent: '#60295f' },
  { name: 'Konkani', native: 'कोंकणी', script: 'Devanagari', accent: '#7d4224' },
  { name: 'Maithili', native: 'मैथिली', script: 'Devanagari', accent: '#864727' },
  { name: 'Malayalam', native: 'മലയാളം', script: 'Malayalam', accent: '#1f5875' },
  // Meiteilon in the Bengali script, which is what Manipuri is published in; Meitei
  // Mayek is the revived alternative and would need a different face.
  { name: 'Manipuri', native: 'মৈতৈলোন্', script: 'Bengali', accent: '#25746a', aliases: ['Meithei'] },
  { name: 'Marathi', native: 'मराठी', script: 'Devanagari', accent: '#904c2a' },
  { name: 'Nepali', native: 'नेपाली', script: 'Devanagari', accent: '#9a512d' },
  { name: 'Odia', native: 'ଓଡ଼ିଆ', script: 'Odia', accent: '#1e666b', aliases: ['Oriya'] },
  { name: 'Punjabi', native: 'ਪੰਜਾਬੀ', script: 'Gurmukhi', accent: '#3e6a25', aliases: ['Panjabi'] },
  { name: 'Sanskrit', native: 'संस्कृतम्', script: 'Devanagari', accent: '#a45630' },
  { name: 'Santali', native: 'ᱥᱟᱱᱛᱟᱲᱤ', script: 'OlChiki', accent: '#616321' },
  { name: 'Sindhi', native: 'سنڌي', script: 'PersoArabic', accent: '#723170' },
  { name: 'Spanish', native: 'Español', script: 'Latin', accent: '#a8701c' },
  { name: 'Tamil', native: 'தமிழ்', script: 'Tamil', accent: '#7c2743' },
  { name: 'Telugu', native: 'తెలుగు', script: 'Telugu', accent: '#673384' },
  { name: 'Urdu', native: 'اردو', script: 'PersoArabic', accent: '#843982' },
];

const DECADES = [1920, 1930, 1940, 1950, 1960, 1970, 1980, 1990, 2000, 2010, 2020];

// ---------------------------------------------------------------------------
// Colour helpers.
// ---------------------------------------------------------------------------

function parseHex(hex) {
  const value = parseInt(hex.replace('#', ''), 16);
  return { r: (value >> 16) & 255, g: (value >> 8) & 255, b: value & 255 };
}

const rgba = ({ r, g, b }, alpha) => `rgba(${r}, ${g}, ${b}, ${alpha})`;

/** Linear blend; amount 0 returns `from`, 1 returns `to`. */
function mix(from, to, amount) {
  const round = (a, b) => Math.round(a + (b - a) * amount);
  return { r: round(from.r, to.r), g: round(from.g, to.g), b: round(from.b, to.b) };
}

/**
 * Decades run warm to cool across the century so a year library reads as ordered at a
 * glance. The hue travels backwards — orange, red, plum, violet, blue — because the
 * forward path runs through yellow and green, which go muddy at this lightness.
 */
function decadeAccent(index, total) {
  const hue = (20 - (165 * index) / (total - 1) + 360) % 360;
  const [r, g, b] = hslToRgb(hue / 360, 0.32, 0.3);
  return { r, g, b };
}

function hslToRgb(h, s, l) {
  const chroma = (1 - Math.abs(2 * l - 1)) * s;
  const secondary = chroma * (1 - Math.abs(((h * 6) % 2) - 1));
  const base = l - chroma / 2;
  const sextant = Math.floor(h * 6) % 6;
  const table = [
    [chroma, secondary, 0],
    [secondary, chroma, 0],
    [0, chroma, secondary],
    [0, secondary, chroma],
    [secondary, 0, chroma],
    [chroma, 0, secondary],
  ][sextant];
  return table.map((channel) => Math.round((channel + base) * 255));
}

// ---------------------------------------------------------------------------
// Drawing helpers.
// ---------------------------------------------------------------------------

/**
 * Largest size at or below `maxSize` that keeps `text` inside the content width.
 * Long country names shrink rather than overflow or get truncated.
 */
function fitFontSize(ctx, text, font, maxSize, minSize) {
  for (let size = maxSize; size > minSize; size--) {
    ctx.font = `${size}px "${font}"`;
    if (ctx.measureText(text).width <= CONTENT_WIDTH) return size;
  }
  return minSize;
}

/**
 * Draws `text` horizontally centred, optically centred on `centreY` rather than boxed.
 *
 * The ascent and descent come from the shaped run, not from the font's global metrics,
 * which is what keeps a Devanagari name carrying a vowel sign above the headstroke and
 * a virama below it sitting on the same optical centre as a bare Latin word.
 */
function drawHero(ctx, text, font, maxSize, minSize, rtl = false) {
  ctx.direction = rtl ? 'rtl' : 'ltr';
  const size = fitFontSize(ctx, text, font, maxSize, minSize);
  ctx.font = `${size}px "${font}"`;
  const metrics = ctx.measureText(text);
  const baseline = HERO_CENTRE + (metrics.actualBoundingBoxAscent - metrics.actualBoundingBoxDescent) / 2;
  ctx.fillStyle = '#ffffff';
  ctx.textAlign = 'center';
  withShadow(ctx, () => ctx.fillText(text, POSTER_WIDTH / 2, baseline));
  ctx.direction = 'ltr';
}

/** The bold white name along the bottom edge. Present on every poster, always here. */
function drawLabel(ctx, text) {
  const size = fitFontSize(ctx, text, 'Latin', 44, 20);
  ctx.font = `${size}px "Latin"`;
  ctx.fillStyle = '#ffffff';
  ctx.textAlign = 'center';
  withShadow(ctx, () => ctx.fillText(text, POSTER_WIDTH / 2, LABEL_BASELINE));
}

/** Keeps text legible over a light patch of flag without darkening the whole poster. */
function withShadow(ctx, draw) {
  ctx.save();
  ctx.shadowColor = 'rgba(0, 0, 0, 0.55)';
  ctx.shadowBlur = 14;
  ctx.shadowOffsetY = 2;
  draw();
  ctx.restore();
}

/** Vertical fade to ink over the bottom of the poster; what the label sits on. */
function drawScrim(ctx) {
  const gradient = ctx.createLinearGradient(0, SCRIM_TOP, 0, POSTER_HEIGHT);
  gradient.addColorStop(0, rgba(INK, 0));
  gradient.addColorStop(0.5, rgba(INK, 0.5));
  gradient.addColorStop(1, rgba(INK, 0.96));
  ctx.fillStyle = gradient;
  ctx.fillRect(0, SCRIM_TOP, POSTER_WIDTH, POSTER_HEIGHT - SCRIM_TOP);
}

/** Flat tinted background for the namespaces that have no imagery of their own. */
function drawTintedBackground(ctx, accent) {
  const gradient = ctx.createLinearGradient(0, 0, 0, POSTER_HEIGHT);
  gradient.addColorStop(0, rgba(mix(accent, { r: 255, g: 255, b: 255 }, 0.16), 1));
  gradient.addColorStop(0.5, rgba(accent, 1));
  gradient.addColorStop(1, rgba(mix(accent, INK, 0.85), 1));
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, POSTER_WIDTH, POSTER_HEIGHT);
}

function newPoster() {
  const canvas = createCanvas(POSTER_WIDTH, POSTER_HEIGHT);
  const ctx = canvas.getContext('2d');
  ctx.fillStyle = rgba(INK, 1);
  ctx.fillRect(0, 0, POSTER_WIDTH, POSTER_HEIGHT);
  return { canvas, ctx };
}

// ---------------------------------------------------------------------------
// Poster builders.
// ---------------------------------------------------------------------------

/**
 * A flag is 4:3 and a poster is 2:3, so cropping one to fill the other would cut a
 * vertical tricolour in half. Instead the flag is drawn flat and whole across the full
 * width, over a blurred and dimmed copy of itself that fills the rest — the letterbox
 * treatment. The flag stays recognisable and the poster still carries its colour.
 */
async function buildCountryPoster({ name, code }) {
  const flagPath = join(modules, 'flag-icons', 'flags', '4x3', `${code}.svg`);
  if (!existsSync(flagPath)) throw new Error(`no flag for ${name}: ${flagPath}`);
  const flagSvg = readFileSync(flagPath);

  const backdrop = await sharp(flagSvg)
    .resize(POSTER_WIDTH, POSTER_HEIGHT, { fit: 'cover' })
    .blur(30)
    .modulate({ brightness: 0.5, saturation: 0.8 })
    .png()
    .toBuffer();

  const flag = await sharp(flagSvg).resize(POSTER_WIDTH, FLAG_HEIGHT, { fit: 'fill' }).png().toBuffer();

  const { canvas, ctx } = newPoster();
  const flagTop = HERO_CENTRE - FLAG_HEIGHT / 2;

  ctx.drawImage(await loadImage(backdrop), 0, 0, POSTER_WIDTH, POSTER_HEIGHT);
  ctx.drawImage(await loadImage(flag), 0, flagTop, POSTER_WIDTH, FLAG_HEIGHT);

  // Hairlines separate the flag from the blur behind it when the two are similar colours.
  ctx.fillStyle = 'rgba(255, 255, 255, 0.16)';
  ctx.fillRect(0, flagTop, POSTER_WIDTH, 1);
  ctx.fillRect(0, flagTop + FLAG_HEIGHT - 1, POSTER_WIDTH, 1);

  drawScrim(ctx);
  drawLabel(ctx, name);
  return canvas;
}

/** Native name as the hero, English name on the label line so both are searchable by eye. */
function buildLanguagePoster({ name, native, script, accent }) {
  const { canvas, ctx } = newPoster();
  drawTintedBackground(ctx, parseHex(accent));
  drawScrim(ctx);
  drawHero(ctx, native, script, 80, 34, SCRIPTS[script].rtl === true);
  drawLabel(ctx, name);
  return canvas;
}

/** Decade as the hero, the years it spans on the label line. */
function buildDecadePoster(startYear, accent) {
  const { canvas, ctx } = newPoster();
  drawTintedBackground(ctx, accent);
  drawScrim(ctx);
  drawHero(ctx, `${startYear}s`, 'Latin', 92, 40);
  drawLabel(ctx, `${startYear}–${startYear + 9}`);
  return canvas;
}

// ---------------------------------------------------------------------------
// Write everything out.
// ---------------------------------------------------------------------------

/**
 * Re-encodes through sharp, which packs the PNG far tighter than the canvas encoder.
 *
 * `names` is the canonical name first, then any alias. Jellyfin hands us whichever
 * English name its metadata carries, and for several Indian languages that is a coin
 * toss — Odia or Oriya, Manipuri or Meithei — so both files exist and both are the same
 * bytes. Encoding once and writing it twice keeps them from drifting apart.
 */
async function writePoster(namespace, names, canvas) {
  const png = await sharp(canvas.toBuffer('image/png')).png({ compressionLevel: 9, effort: 10 }).toBuffer();
  return [names].flat().map((name) => {
    const fileSlug = slug(name);
    if (!fileSlug) throw new Error(`empty slug for ${namespace}/${name}`);
    const path = join(outputRoot, namespace, `${fileSlug}.png`);
    writeFileSync(path, png);
    return { path, bytes: png.length };
  });
}

const written = [];

for (const namespace of ['origin', 'lang', 'year']) {
  // Regenerate from scratch: a renamed value would otherwise leave a stale poster behind.
  rmSync(join(outputRoot, namespace), { recursive: true, force: true });
  mkdirSync(join(outputRoot, namespace), { recursive: true });
}

for (const country of COUNTRIES) {
  written.push(...(await writePoster('origin', country.name, await buildCountryPoster(country))));
}

for (const language of LANGUAGES) {
  const names = [language.name, ...(language.aliases ?? [])];
  written.push(...(await writePoster('lang', names, buildLanguagePoster(language))));
}

for (const [index, startYear] of DECADES.entries()) {
  const canvas = buildDecadePoster(startYear, decadeAccent(index, DECADES.length));
  written.push(...(await writePoster('year', `${startYear}s`, canvas)));
}

// ---------------------------------------------------------------------------
// Verify what actually landed on disk rather than trusting the encoder.
// ---------------------------------------------------------------------------

// A failed drawImage or a missing glyph does not throw — it just leaves a flat rectangle
// of background. So as well as the header, check that the hero band actually has
// something in it. Anything real varies by far more than this floor.
const MIN_HERO_CONTRAST = 4;

let malformed = 0;

// A poster whose face does not carry the script still passes the contrast check — a row
// of tofu boxes has plenty of contrast. Nor does measureText help: Skia hands back the
// .notdef advance rather than zero, so a missing glyph measures like a present one.
//
// What does distinguish them is the raster. Render the name, then render the same number
// of U+FFFF — a permanently unassigned codepoint, so guaranteed .notdef in every face —
// at the same size in the same face. If the two come out pixel for pixel identical, then
// every glyph in the name was .notdef and the alias is pointing at a subset that does not
// contain the script. Anything the face can actually draw breaks the match.
{
  const notdefProbe = (script, text) => {
    const canvas = createCanvas(600, 120);
    const ctx = canvas.getContext('2d');
    ctx.font = `60px "${script}"`;
    ctx.fillText(text, 5, 90);
    return canvas.toBuffer('image/png');
  };

  for (const { name, native, script } of LANGUAGES) {
    const drawn = notdefProbe(script, native);
    const tofu = notdefProbe(script, '￿'.repeat([...native].length));
    if (drawn.equals(tofu)) {
      console.warn(`  no glyphs: lang/${slug(name)} — "${native}" is all tofu in ${script}`);
      malformed++;
    }
  }
}

for (const { path } of written) {
  const metadata = await sharp(path).metadata();
  if (metadata.format !== 'png' || metadata.width !== POSTER_WIDTH || metadata.height !== POSTER_HEIGHT) {
    console.warn(`  wrong image: ${path} (${metadata.format} ${metadata.width}x${metadata.height})`);
    malformed++;
    continue;
  }

  const hero = await sharp(path)
    .extract({ left: 0, top: HERO_CENTRE - FLAG_HEIGHT / 2, width: POSTER_WIDTH, height: FLAG_HEIGHT })
    .stats();
  const contrast = Math.max(...hero.channels.map((channel) => channel.stdev));
  if (contrast < MIN_HERO_CONTRAST) {
    console.warn(`  blank hero: ${path} (contrast ${contrast.toFixed(1)})`);
    malformed++;
  }
}

const counts = ['origin', 'lang', 'year']
  .map((namespace) => `${namespace}=${readdirSync(join(outputRoot, namespace)).length}`)
  .join(' ');

const totalBytes = written.reduce((sum, { bytes }) => sum + bytes, 0);

console.log(
  `posters=${written.length} ${counts} malformed=${malformed} ` +
    `bytes=${totalBytes} out=assets/thumbnails`
);
