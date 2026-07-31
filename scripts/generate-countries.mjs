// Builds the embedded country alias dictionary from Unicode CLDR.
//
//   npm --prefix scripts install
//   node scripts/generate-countries.mjs
//
// Output: Jellyfin.Plugin.Tagsmith/Data/countries.json.gz — a flat map of
// alias slug -> canonical English name slug, covering every ISO 3166-1 territory in
// every CLDR locale, plus alpha-2 and alpha-3 codes.
//
// Aliases that would resolve to more than one country (CLDR renders both Congos
// identically in some locales, for instance) are dropped rather than guessed.

import { readFileSync, writeFileSync, readdirSync, mkdirSync } from 'node:fs';
import { gzipSync } from 'node:zlib';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const modules = process.env.CLDR_MODULES ?? join(here, 'node_modules');
const localeRoot = join(modules, 'cldr-localenames-modern', 'main');
const outputPath = join(here, '..', 'Jellyfin.Plugin.Tagsmith', 'Data', 'countries.json.gz');

/** Mirrors TagNormalizer.Slug in the C# side; the two must stay in step. */
function slug(value) {
  if (!value) return '';
  const stripped = value.trim().toLowerCase().normalize('NFD').replace(/\p{Mn}/gu, '');
  return stripped
    .replace(/[^\p{L}\p{N}]+/gu, '_')
    .replace(/^_+|_+$/g, '')
    .normalize('NFC');
}

const codeMappings = JSON.parse(
  readFileSync(join(modules, 'cldr-core', 'supplemental', 'codeMappings.json'), 'utf8')
).supplemental.codeMappings;

// ISO 3166-1 territories only: two-letter codes carrying an alpha-3. This excludes
// CLDR's macro-regions (001, 419), the EU/EZ/UN groupings and private-use codes.
const territories = Object.entries(codeMappings)
  .filter(([code, data]) => /^[A-Z]{2}$/.test(code) && data._alpha3)
  .map(([code, data]) => ({ code, alpha3: data._alpha3 }));

const english = JSON.parse(
  readFileSync(join(localeRoot, 'en', 'territories.json'), 'utf8')
).main.en.localeDisplayNames.territories;

// CLDR's primary English name is sometimes an administrative mouthful ("Hong Kong SAR
// China", "Palestinian Territories"). Where the short form is a real name rather than an
// abbreviation — i.e. it contains lowercase letters, ruling out "US", "UK", "UAE" — use
// it as the canonical value instead.
const canonical = new Map();
for (const { code } of territories) {
  const short = english[code + '-alt-short'];
  const name = short && /\p{Ll}/u.test(short) ? short : english[code];
  if (!name) continue;
  const canonicalSlug = slug(name);
  if (canonicalSlug) canonical.set(code, canonicalSlug);
}

// alias slug -> set of canonical slugs it could mean
const candidates = new Map();
const record = (alias, target) => {
  if (!alias || !target) return;
  let bucket = candidates.get(alias);
  if (!bucket) candidates.set(alias, (bucket = new Set()));
  bucket.add(target);
};

// ISO codes are unambiguous by definition, so they bypass the collision handling below:
// RU must resolve to Russia even though Catalan renders the UK's short name as "RU", and
// NGA to Nigeria even though "Nga" is Vietnamese for Russia. This matters since TMDb's
// origin_country and TVDb's OriginalCountry are bare codes, not names.
const isoCodes = new Map();

for (const { code, alpha3 } of territories) {
  const target = canonical.get(code);
  if (!target) continue;
  isoCodes.set(slug(code), target);
  isoCodes.set(slug(alpha3), target);
  record(slug(code), target);
  record(slug(alpha3), target);
  record(target, target);
}

// Curated additions CLDR does not carry (ISO official long names, colloquialisms).
const extra = JSON.parse(readFileSync(join(here, 'country-aliases-extra.json'), 'utf8'));
let extraCount = 0;

for (const [alias, code] of Object.entries(extra)) {
  if (alias.startsWith('_')) continue;
  const target = canonical.get(code);
  if (!target) {
    console.warn(`  unknown territory code in extras: ${code} (${alias})`);
    continue;
  }

  record(slug(alias), target);
  extraCount++;
}

const locales = readdirSync(localeRoot);
let localeCount = 0;

for (const locale of locales) {
  let names;
  try {
    const parsed = JSON.parse(readFileSync(join(localeRoot, locale, 'territories.json'), 'utf8'));
    names = parsed.main[locale].localeDisplayNames.territories;
  } catch {
    continue;
  }

  localeCount++;

  for (const { code } of territories) {
    const target = canonical.get(code);
    if (!target) continue;

    for (const suffix of ['', '-alt-short', '-alt-variant', '-alt-stand-alone']) {
      record(slug(names[code + suffix]), target);
    }
  }
}

const map = {};
let ambiguous = 0;

for (const [alias, targets] of [...candidates.entries()].sort(([a], [b]) => (a < b ? -1 : 1))) {
  if (isoCodes.has(alias)) {
    // An ISO code wins its own country outright. Without this, six alpha-2 codes and one
    // alpha-3 (AO AS BI KM RU SA, NGA) collided with some locale's display name for a
    // different country and were dropped as ambiguous — so a Russian series coming from
    // TMDb as "RU" tagged origin=ru while a Russian film tagged origin=russia.
    map[alias] = isoCodes.get(alias);
    if (targets.size > 1) ambiguous++;
  } else if (targets.size === 1) {
    map[alias] = [...targets][0];
  } else if (targets.has(alias)) {
    // A canonical name that also reads as an alias of something else wins for itself.
    map[alias] = alias;
    ambiguous++;
  } else {
    ambiguous++;
  }
}

mkdirSync(dirname(outputPath), { recursive: true });
writeFileSync(outputPath, gzipSync(Buffer.from(JSON.stringify(map)), { level: 9 }));

console.log(
  `territories=${territories.length} locales=${localeCount} curated=${extraCount} ` +
    `aliases=${Object.keys(map).length} ambiguous_dropped=${ambiguous} ` +
    `bytes=${readFileSync(outputPath).length}`
);
