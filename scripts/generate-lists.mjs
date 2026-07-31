// Builds the embedded curated-lists dataset: IMDb title id -> [list slugs].
//
//   node scripts/generate-lists.mjs
//
// Output: Jellyfin.Plugin.Tagsmith/Data/lists.json.gz
//
// The lists are snapshots taken at generation time — the IMDb Top 250 as of the release,
// not as of tonight — which is the honest thing an offline tag can be. Sources:
//
//   - imdb_top_250            IMDb's chart page embeds the full list as JSON-LD. IMDb
//                             bot-blocks plain fetches, so the latest Wayback Machine
//                             snapshot is used; only the 250 tt ids are extracted (facts,
//                             not IMDb's copyrighted prose).
//   - sight_and_sound         The 2022 Sight & Sound critics' poll, from
//                             github.com/samMint/Sight-and-Sound-Film-Data (MIT). The CSV
//                             has no IMDb ids; titles+years resolve through Wikidata, with
//                             scripts/list-overrides.json for the stragglers.
//   - afi_100                 AFI's 100 Years…100 Movies (10th Anniversary), parsed from
//                             the Wikipedia article and joined to IMDb via Wikidata
//                             sitelinks.
//   - bfi_top_100             The BFI Top 100 British films, same recipe as afi_100.
//   - national_film_registry  Wikidata: films that are part of (P361) the registry.
//   - criterion_collection    Wikidata: films carrying a Criterion spine number (P12279).
//   - tspdt_1000              They Shoot Pictures, Don't They? top 1000, via
//                             github.com/li4alex/tspdt-tmdb (TMDB ids), joined to IMDb
//                             through Wikidata's TMDb-id properties (P4947/P4983).
//
// Every unresolved title is reported at the end; fix persistent ones by adding an entry
// to scripts/list-overrides.json rather than editing the output.

import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { gzipSync } from 'node:zlib';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const outputPath = join(here, '..', 'Jellyfin.Plugin.Tagsmith', 'Data', 'lists.json.gz');
const userAgent = 'TagsmithGenerator/0.1 (+https://github.com/aaronified/jellyfin-tagsmith)';

const overrides = JSON.parse(readFileSync(join(here, 'list-overrides.json'), 'utf8'));

const lists = new Map(); // list slug -> Set of tt ids
const problems = [];

function addAll(listSlug, ids) {
  let bucket = lists.get(listSlug);
  if (!bucket) lists.set(listSlug, (bucket = new Set()));
  for (const id of ids) {
    const clean = String(id).trim().toLowerCase();
    if (/^tt\d+$/.test(clean)) bucket.add(clean);
  }
}

async function fetchText(url, headers = {}) {
  const response = await fetch(url, { headers: { 'user-agent': userAgent, ...headers }, redirect: 'follow' });
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}: ${url}`);
  return response.text();
}

async function sparql(query) {
  const url = 'https://query.wikidata.org/sparql?format=json&query=' + encodeURIComponent(query);
  const response = await fetch(url, { headers: { 'user-agent': userAgent, accept: 'application/sparql-results+json' } });
  if (!response.ok) throw new Error(`SPARQL ${response.status}`);
  return (await response.json()).results.bindings;
}

const chunk = (array, size) =>
  Array.from({ length: Math.ceil(array.length / size) }, (_, i) => array.slice(i * size, (i + 1) * size));

/** First usable IMDb id on an entity — some carry a deprecated or empty first claim. */
const imdbOf = (entity) =>
  entity?.claims?.P345?.map((c) => c?.mainsnak?.datavalue?.value)
    .find((v) => typeof v === 'string' && v.startsWith('tt'));

/** Publication years on an entity, for verifying a search hit. */
const yearsOf = (entity) =>
  (entity?.claims?.P577 ?? [])
    .map((c) => c?.mainsnak?.datavalue?.value?.time?.match(/^\+(\d{4})/)?.[1])
    .filter(Boolean)
    .map(Number);

/**
 * Last-resort resolver: full-text entity search, verified against the year when one is
 * known. Slow (two calls per title), so it only ever runs for the stragglers the batch
 * lookups missed — title-case drift, punctuation variants, translated titles.
 */
async function searchResolve(title, year) {
  const search = JSON.parse(await fetchText(
    'https://www.wikidata.org/w/api.php?action=wbsearchentities&language=en&type=item&limit=10&format=json&search='
    + encodeURIComponent(title)));

  const ids = (search.search ?? []).map((hit) => hit.id);
  if (ids.length === 0) return null;

  const data = JSON.parse(await fetchText(
    'https://www.wikidata.org/w/api.php?action=wbgetentities&props=claims&format=json&ids='
    + ids.join('|')));

  for (const id of ids) {
    const entity = data.entities?.[id];
    const imdb = imdbOf(entity);
    if (!imdb) continue;

    if (year === undefined) return imdb;

    const years = yearsOf(entity);
    if (years.some((y) => Math.abs(y - year) <= 1)) return imdb;
  }

  return null;
}

// ---------------------------------------------------------------- imdb_top_250

async function imdbTop250() {
  // The live page sits behind a bot challenge; the Wayback Machine keeps near-daily
  // snapshots. The /web/2/ prefix means "newest capture whose timestamp starts with 2" —
  // i.e. the latest one, forever. A year prefix would pin regenerations to that year's
  // last capture and silently go stale.
  const html = await fetchText('https://web.archive.org/web/2/https://www.imdb.com/chart/top/');

  const jsonLd = html.match(/<script type="application\/ld\+json">(.*?)<\/script>/s)?.[1];
  if (!jsonLd) throw new Error('imdb_top_250: no JSON-LD block in the snapshot');

  const chart = JSON.parse(jsonLd);
  const ids = (chart.itemListElement ?? [])
    .map((element) => element?.item?.url?.match(/title\/(tt\d+)/)?.[1])
    .filter(Boolean);

  if (ids.length !== 250) throw new Error(`imdb_top_250: expected 250 ids, got ${ids.length}`);
  addAll('imdb_top_250', ids);
  console.log('imdb_top_250: 250');
}

// ---------------------------------------------------------------- title+year -> tt

/**
 * Resolves (title, year) pairs to IMDb ids through Wikidata labels, then alt-labels for
 * the leftovers. Exact, case-sensitive matching — that is what keeps the label lookup on
 * the index — so a per-list override map handles the inevitable stragglers.
 */
async function resolveTitles(listSlug, pairs) {
  const wanted = new Map(pairs.map(({ title, year }) => [`${title} ${year}`, { title, year }]));
  const resolved = new Map(); // key -> Set of distinct imdb ids

  for (const predicate of ['rdfs:label', 'skos:altLabel']) {
    const missing = [...wanted.values()].filter(({ title, year }) => !resolved.has(`${title} ${year}`));
    if (missing.length === 0) break;

    for (const group of chunk(missing, 40)) {
      const values = group
        .map(({ title, year }) => `(${JSON.stringify(title)}@en ${year})`)
        .join(' ');

      const rows = await sparql(`
        SELECT DISTINCT ?lbl ?y ?imdb WHERE {
          VALUES (?lbl ?y) { ${values} }
          ?film ${predicate} ?lbl ; wdt:P345 ?imdb ; wdt:P577 ?date .
          ?film wdt:P31/wdt:P279* wd:Q11424 .
          FILTER(ABS(YEAR(?date) - ?y) <= 1)
          FILTER(STRSTARTS(?imdb, "tt"))
        }`);

      for (const row of rows) {
        const key = `${row.lbl.value} ${row.y.value}`;
        let bucket = resolved.get(key);
        if (!bucket) resolved.set(key, (bucket = new Set()));
        bucket.add(row.imdb.value);
      }
    }
  }

  const ids = [];
  for (const [key, { title, year }] of wanted) {
    const overridden = overrides[listSlug]?.[`${title} (${year})`];
    const matches = resolved.get(key);

    // Same policy as the country dictionary: an ambiguous match is reported, never
    // guessed at. The override map is where a human settles it.
    if (!overridden && matches && matches.size > 1) {
      problems.push(`${listSlug}: ambiguous "${title}" (${year}) — ${[...matches].join(', ')}`);
      continue;
    }

    let id = overridden ?? (matches ? [...matches][0] : undefined);
    id ??= await searchResolve(title, year);

    if (id) ids.push(id);
    else problems.push(`${listSlug}: unresolved "${title}" (${year})`);
  }

  return ids;
}

// ---------------------------------------------------------------- sight_and_sound

/** Minimal quote-aware CSV row parser — the dataset has quoted, comma-bearing titles. */
function parseCsv(text) {
  const rows = [];
  let row = [];
  let cell = '';
  let quoted = false;

  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"' && text[i + 1] === '"') { cell += '"'; i++; }
      else if (c === '"') quoted = false;
      else cell += c;
    } else if (c === '"') quoted = true;
    else if (c === ',') { row.push(cell); cell = ''; }
    else if (c === '\n' || c === '\r') {
      if (cell.length > 0 || row.length > 0) { row.push(cell); rows.push(row); row = []; cell = ''; }
    } else cell += c;
  }

  if (cell.length > 0 || row.length > 0) { row.push(cell); rows.push(row); }
  return rows;
}

async function sightAndSound() {
  const csv = await fetchText(
    'https://raw.githubusercontent.com/samMint/Sight-and-Sound-Film-Data/main/dataset.csv');

  const rows = parseCsv(csv);
  const header = rows[0].map((h) => h.trim()); // the first column is literally "title "
  const title = header.indexOf('title');
  const year = header.indexOf('year');

  const pairs = rows.slice(1)
    .filter((r) => r[title] && /^\d{4}$/.test(r[year] ?? ''))
    .map((r) => ({ title: r[title].trim(), year: Number(r[year]) }));

  const ids = await resolveTitles('sight_and_sound', pairs);
  addAll('sight_and_sound', ids);
  console.log(`sight_and_sound: ${ids.length} of ${pairs.length}`);
}

// ---------------------------------------------------------------- Wikipedia ranked lists

/**
 * Parses a Wikipedia ranked-table article into enwiki article titles, in rank order.
 * Handles the two table shapes these lists use:
 *
 *   | 1.                              a rank cell on its own line (AFI); the film's
 *   | ''[[Citizen Kane]]''            wikilink is on the next line, sometimes behind a
 *                                     {{Hs|…}} sort-key template
 *
 *   | 1 || ''[[The Third Man]]'' || … one row per line, cells split by || (BFI)
 *
 * Deduplicated by rank keeping the first occurrence, because the articles carry further
 * tables (year-by-year changes, the 1998 edition) whose cells also look numeric.
 */
async function wikipediaRankedTitles(page, expected) {
  const api = 'https://en.wikipedia.org/w/api.php?action=parse&prop=wikitext&format=json&formatversion=2&page='
    + encodeURIComponent(page);
  const wikitext = JSON.parse(await fetchText(api)).parse.wikitext;
  const lines = wikitext.split('\n');

  // Each shape is tried as its own pass over the whole page, most specific first, and the
  // first pass that yields exactly the expected rank set wins. Mixing shapes in one pass
  // let a secondary table (the top-ten summary, the year-by-year changes) shadow the main
  // list.
  const shapes = [
    // | 1.        rank cell alone, with a dot (AFI); film wikilink on the next line,
    // | ''[[…]]'' sometimes behind a {{Hs|…}} sort-key template.
    (line, next) => {
      const match = line.match(/^\|\s*(\d+)\.\s*$/);
      return match ? [Number(match[1]), next?.match(/\[\[([^\]|#]+)/)?.[1]] : null;
    },
    // |1          rank cell alone, no dot (BFI main table); film on the next line.
    (line, next) => {
      const match = line.match(/^\|\s*(\d+)\s*$/);
      return match ? [Number(match[1]), next?.match(/^\|.*?\[\[([^\]|#]+)/)?.[1]] : null;
    },
    // | 1 || ''[[…]]'' || …   the whole row on one line, film in the second cell.
    (line) => {
      const match = line.match(/^\|\s*(\d+)\s*\|\|(.*)/);
      return match ? [Number(match[1]), match[2].split('||')[0]?.match(/\[\[([^\]|#]+)/)?.[1]] : null;
    }
  ];

  for (const shape of shapes) {
    const byRank = new Map();

    for (let i = 0; i < lines.length; i++) {
      const entry = shape(lines[i], lines[i + 1]);
      if (!entry) continue;

      const [rank, link] = entry;
      if (link && rank >= 1 && rank <= expected && !byRank.has(rank)) {
        byRank.set(rank, link.trim());
      }
    }

    if (byRank.size === expected) {
      return [...byRank.entries()].sort(([a], [b]) => a - b).map(([, title]) => title);
    }
  }

  throw new Error(`${page}: no table shape yielded ${expected} ranked entries`);
}

/** Resolves enwiki article titles to IMDb ids via Wikidata sitelinks. */
async function wikipediaTitlesToImdb(listSlug, titles) {
  const ids = [];
  const found = new Map();

  for (const group of chunk(titles, 50)) {
    const api = 'https://www.wikidata.org/w/api.php?action=wbgetentities&props=claims|sitelinks&format=json&sites=enwiki&titles='
      + encodeURIComponent(group.join('|'));
    const data = JSON.parse(await fetchText(api));

    for (const entity of Object.values(data.entities ?? {})) {
      const article = entity?.sitelinks?.enwiki?.title;
      const imdb = imdbOf(entity);
      if (article && imdb) found.set(article, imdb);
    }

    // The API normalises titles (underscores, casing); map those back too.
    for (const { from, to } of data.normalized ?? []) {
      if (found.has(to)) found.set(from, found.get(to));
    }
  }

  for (const title of titles) {
    let id = overrides[listSlug]?.[title] ?? found.get(title);
    // The search fallback strips a " (1957 film)"-style disambiguator — the entity search
    // matches on the plain title — but keeps its year, when it has one, as verification.
    const disambiguatorYear = title.match(/\((\d{4})[^)]*\)\s*$/)?.[1];
    id ??= await searchResolve(
      title.replace(/\s*\([^)]*\)\s*$/, ''),
      disambiguatorYear ? Number(disambiguatorYear) : undefined);

    if (id) ids.push(id);
    else problems.push(`${listSlug}: unresolved "${title}"`);
  }

  return ids;
}

async function wikipediaList(listSlug, page, expected) {
  const titles = await wikipediaRankedTitles(page, expected);
  const ids = await wikipediaTitlesToImdb(listSlug, titles);
  addAll(listSlug, ids);
  console.log(`${listSlug}: ${ids.length} of ${expected}`);
}

// ---------------------------------------------------------------- Wikidata memberships

async function nationalFilmRegistry() {
  const rows = await sparql(`
    SELECT DISTINCT ?imdb WHERE {
      ?film p:P361 ?st . ?st ps:P361 wd:Q823422 .
      ?film wdt:P345 ?imdb . FILTER(STRSTARTS(?imdb, "tt"))
    }`);
  addAll('national_film_registry', rows.map((r) => r.imdb.value));
  console.log(`national_film_registry: ${rows.length}`);
}

async function criterionCollection() {
  const rows = await sparql(`
    SELECT DISTINCT ?imdb WHERE {
      ?film wdt:P12279 ?spine ; wdt:P345 ?imdb . FILTER(STRSTARTS(?imdb, "tt"))
    }`);
  addAll('criterion_collection', rows.map((r) => r.imdb.value));
  console.log(`criterion_collection: ${rows.length}`);
}

// ---------------------------------------------------------------- tspdt_1000

async function tspdt() {
  const entries = JSON.parse(await fetchText(
    'https://raw.githubusercontent.com/li4alex/tspdt-tmdb/main/public/movie_data_with_ids.json'));

  // P4947 is Wikidata's TMDb *film* id, P4983 its TMDb *series* id.
  const byProperty = { P4947: [], P4983: [] };
  for (const entry of entries) {
    const id = entry['TMDB ID'];
    if (!id) continue;
    (entry['Media Type'] === 'tv' ? byProperty.P4983 : byProperty.P4947).push(entry);
  }

  const found = new Map(); // tmdb id -> tt

  for (const [property, group] of Object.entries(byProperty)) {
    for (const slice of chunk(group, 100)) {
      const values = slice.map((e) => `"${e['TMDB ID']}"`).join(' ');
      const rows = await sparql(`
        SELECT DISTINCT ?tmdb ?imdb WHERE {
          VALUES ?tmdb { ${values} }
          ?film wdt:${property} ?tmdb ; wdt:P345 ?imdb . FILTER(STRSTARTS(?imdb, "tt"))
        }`);
      for (const row of rows) found.set(`${property}:${row.tmdb.value}`, row.imdb.value);
    }
  }

  const ids = [];
  for (const entry of entries) {
    const property = entry['Media Type'] === 'tv' ? 'P4983' : 'P4947';
    let id = overrides.tspdt_1000?.[`tmdb:${entry['TMDB ID']}`] ?? found.get(`${property}:${entry['TMDB ID']}`);

    // A TMDb id Wikidata has not catalogued; fall back to title+year search. The year
    // field can be a range ("1988-98"), so only its leading year verifies.
    const year = String(entry.Year ?? '').match(/^\d{4}/)?.[0];
    id ??= await searchResolve(entry.Title, year ? Number(year) : undefined);

    if (id) ids.push(id);
    else problems.push(`tspdt_1000: unresolved "${entry.Title}" (${entry.Year}, tmdb ${entry['TMDB ID']})`);
  }

  addAll('tspdt_1000', ids);
  console.log(`tspdt_1000: ${ids.length} of ${entries.length}`);
}

// ---------------------------------------------------------------- run

await imdbTop250();
await sightAndSound();
await wikipediaList('afi_100', "AFI's 100 Years...100 Movies (10th Anniversary Edition)", 100);
await wikipediaList('bfi_top_100', 'BFI Top 100 British films', 100);
await nationalFilmRegistry();
await criterionCollection();
await tspdt();

if (problems.length > 0) {
  console.log(`\n${problems.length} unresolved (add to list-overrides.json):`);
  for (const problem of problems) console.log('  ' + problem);
}

// Floors, not exact counts. A list far below its floor means a source moved or a query
// timed out with partial results, and shipping that would make the next sync DELETE the
// missing list= tags from every user's library. A .gz diff shows a byte count and nothing
// else, so the guard lives here.
const floors = {
  imdb_top_250: 250,
  sight_and_sound: 250,
  afi_100: 100,
  bfi_top_100: 100,
  national_film_registry: 700,
  criterion_collection: 1000,
  tspdt_1000: 900
};

for (const [listSlug, floor] of Object.entries(floors)) {
  const count = lists.get(listSlug)?.size ?? 0;
  if (count < floor) {
    throw new Error(`${listSlug}: only ${count} titles (floor ${floor}) — refusing to write a collapsed dataset`);
  }
}

// Invert to title -> [lists], the shape the plugin reads.
const byTitle = new Map();
for (const [listSlug, ids] of lists) {
  for (const id of ids) {
    let entry = byTitle.get(id);
    if (!entry) byTitle.set(id, (entry = []));
    entry.push(listSlug);
  }
}

const output = {};
for (const [id, slugs] of [...byTitle.entries()].sort(([a], [b]) => (a < b ? -1 : 1))) {
  output[id] = slugs.sort();
}

mkdirSync(dirname(outputPath), { recursive: true });
writeFileSync(outputPath, gzipSync(Buffer.from(JSON.stringify(output)), { level: 9 }));

console.log(
  `\nlists=${lists.size} titles=${byTitle.size} bytes=${readFileSync(outputPath).length}`
);
