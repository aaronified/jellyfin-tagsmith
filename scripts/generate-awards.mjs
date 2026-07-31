// Builds the embedded awards dataset: IMDb title id -> { w: [wins], n: [nominations] },
// values shaped `<ceremony>:<category>` with every segment slugged.
//
//   node scripts/generate-awards.mjs
//
// Output: Jellyfin.Plugin.Tagsmith/Data/awards.json.gz
//
// Sources, all fetched at generation time — the plugin itself never talks to the network:
//
//   - Academy Awards: github.com/DLu/oscar_data (BSD-2-Clause), a tab-separated table of
//     every nomination since 1927/28 with IMDb title ids and a CanonicalCategory column
//     that already folds historical renames.
//   - BAFTA Film Awards, Golden Globes, Primetime Emmys: Wikidata (CC0), via SPARQL over
//     `award received` (P166) and `nominated for` (P1411), keyed by IMDb id (P345).
//     Wikidata's coverage is winner-heavy and recent-weighted; the dataset is honest about
//     that rather than pretending to completeness.
//
// Which categories become tags is decided by scripts/award-categories.json — a curated
// mapping, because tag values are a schema: category slugs are chosen once and kept, not
// scraped. Categories not in the mapping are skipped and reported at the end so additions
// are a deliberate one-line change.

import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { gzipSync } from 'node:zlib';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const outputPath = join(here, '..', 'Jellyfin.Plugin.Tagsmith', 'Data', 'awards.json.gz');
const userAgent = 'TagsmithGenerator/0.1 (+https://github.com/aaronified/jellyfin-tagsmith)';

const categories = JSON.parse(readFileSync(join(here, 'award-categories.json'), 'utf8'));

/** Mirrors TagNormalizer.Slug in the C# side; the two must stay in step. */
function slug(value) {
  if (!value) return '';
  const stripped = value.trim().toLowerCase().normalize('NFD').replace(/\p{Mn}/gu, '');
  return stripped
    .replace(/[^\p{L}\p{N}]+/gu, '_')
    .replace(/^_+|_+$/g, '')
    .normalize('NFC');
}

async function fetchText(url) {
  const response = await fetch(url, { headers: { 'user-agent': userAgent } });
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}: ${url}`);
  return response.text();
}

// tt id -> { w: Set, n: Set }. A win is also a nomination, so the nominee set for a
// category is complete on its own.
const titles = new Map();

function add(imdbId, value, won) {
  const id = imdbId.trim().toLowerCase();
  if (!/^tt\d+$/.test(id)) return;

  let entry = titles.get(id);
  if (!entry) titles.set(id, (entry = { w: new Set(), n: new Set() }));

  entry.n.add(value);
  if (won) entry.w.add(value);
}

// ---------------------------------------------------------------- Academy Awards

const unmappedOscar = new Map();

async function collectOscars() {
  const tsv = await fetchText('https://raw.githubusercontent.com/DLu/oscar_data/main/oscars.csv');
  const lines = tsv.split('\n').filter((l) => l.length > 0);
  const header = lines[0].split('\t');
  const column = Object.fromEntries(header.map((name, i) => [name, i]));

  let rows = 0;
  for (const line of lines.slice(1)) {
    const cells = line.split('\t');
    const canonical = cells[column.CanonicalCategory];
    const filmIds = cells[column.FilmId];
    if (!filmIds) continue;

    const mapped = categories.oscar[canonical];
    if (mapped === undefined) {
      unmappedOscar.set(canonical, (unmappedOscar.get(canonical) ?? 0) + 1);
      continue;
    }
    if (mapped === null) continue; // explicitly skipped (shorts, SciTech, honorary…)

    const won = cells[column.Winner] === 'True';
    for (const id of filmIds.split('|')) add(id, `oscar:${slug(mapped)}`, won);
    rows++;
  }

  console.log(`oscars: ${rows} nominations mapped`);
  return rows;
}

// ---------------------------------------------------------------- Wikidata ceremonies

async function sparql(query) {
  const url = 'https://query.wikidata.org/sparql?format=json&query=' + encodeURIComponent(query);
  const response = await fetch(url, { headers: { 'user-agent': userAgent, accept: 'application/sparql-results+json' } });
  if (!response.ok) throw new Error(`SPARQL ${response.status}: ${query.slice(0, 80)}…`);
  return (await response.json()).results.bindings;
}

async function collectCeremony(ceremony, rootQid) {
  const mapping = categories[ceremony];
  let mapped = 0;
  const unmapped = new Map(); // qid -> { label, count }

  // P166 = award received (a win), P1411 = nominated for.
  for (const [property, won] of [['P166', true], ['P1411', false]]) {
    const rows = await sparql(`
      SELECT ?imdb ?award ?awardLabel WHERE {
        { ?award wdt:P361 wd:${rootQid} } UNION { ?award wdt:P31 wd:${rootQid} }
        ?film p:${property} ?st . ?st ps:${property} ?award .
        ?film wdt:P345 ?imdb . FILTER(STRSTARTS(?imdb, "tt"))
        SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
      }`);

    for (const row of rows) {
      const qid = row.award.value.split('/').pop();
      const category = mapping[qid];
      if (category === undefined) {
        const entry = unmapped.get(qid) ?? { label: row.awardLabel?.value ?? qid, count: 0 };
        entry.count++;
        unmapped.set(qid, entry);
        continue;
      }
      if (category === null) continue;

      add(row.imdb.value, `${ceremony}:${slug(category)}`, won);
      mapped++;
    }
  }

  if (unmapped.size > 0) {
    console.log(`unmapped ${ceremony} categories (add to award-categories.json or map to null):`);
    for (const [qid, { label, count }] of [...unmapped.entries()].sort((a, b) => b[1].count - a[1].count)) {
      console.log(`  ${count}\t${qid}\t${label}`);
    }
  }

  console.log(`${ceremony}: ${mapped} statements mapped, ${unmapped.size} category items skipped`);
  return mapped;
}

// ---------------------------------------------------------------- run

/**
 * Floors, not exact counts: the sources grow. A run far below the floor means the source
 * moved or broke, and shipping its output would make the next sync DELETE the missing
 * tags from every user's library — the prefixes stay managed while the desired set
 * collapses. A .gz diff shows a byte count and nothing else, so the guard lives here.
 */
function assertFloor(name, actual, floor) {
  if (actual < floor) {
    throw new Error(`${name}: only ${actual} mapped (floor ${floor}) — refusing to write a collapsed dataset`);
  }
}

assertFloor('oscars', await collectOscars(), 8000);
assertFloor('bafta', await collectCeremony('bafta', categories._roots.bafta), 250);
assertFloor('golden_globe', await collectCeremony('golden_globe', categories._roots.golden_globe), 350);
assertFloor('emmy', await collectCeremony('emmy', categories._roots.emmy), 200);

if (unmappedOscar.size > 0) {
  console.log('unmapped oscar canonical categories (add to award-categories.json or map to null):');
  for (const [name, count] of [...unmappedOscar.entries()].sort((a, b) => b[1] - a[1])) {
    console.log(`  ${count}\t${name}`);
  }
}

assertFloor('titles', titles.size, 3500);

const output = {};
for (const [id, entry] of [...titles.entries()].sort(([a], [b]) => (a < b ? -1 : 1))) {
  output[id] = { w: [...entry.w].sort(), n: [...entry.n].sort() };
}

mkdirSync(dirname(outputPath), { recursive: true });
writeFileSync(outputPath, gzipSync(Buffer.from(JSON.stringify(output)), { level: 9 }));

const wins = [...titles.values()].reduce((sum, e) => sum + e.w.size, 0);
const nominations = [...titles.values()].reduce((sum, e) => sum + e.n.size, 0);
console.log(
  `titles=${titles.size} wins=${wins} nominations=${nominations} bytes=${readFileSync(outputPath).length}`
);
