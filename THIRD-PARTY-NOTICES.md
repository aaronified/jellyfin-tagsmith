# Third-party data notices

Tagsmith embeds two generated datasets — `Data/awards.json.gz` and `Data/lists.json.gz` —
derived from the sources below. Only facts travel: IMDb title identifiers, award
category membership, and list membership. No ranks, ratings, prose, or other expressive
content from any source is redistributed. The generators live in `scripts/`; regenerate
with `node scripts/generate-awards.mjs` and `node scripts/generate-lists.mjs`.

## Academy Awards data

Derived from [DLu/oscar_data](https://github.com/DLu/oscar_data), used under the
BSD 2-Clause License:

> BSD 2-Clause License
>
> Copyright (c) 2022, David V. Lu!!
> All rights reserved.
>
> Redistribution and use in source and binary forms, with or without modification, are
> permitted provided that the following conditions are met:
>
> 1. Redistributions of source code must retain the above copyright notice, this list of
>    conditions and the following disclaimer.
> 2. Redistributions in binary form must reproduce the above copyright notice, this list
>    of conditions and the following disclaimer in the documentation and/or other
>    materials provided with the distribution.
>
> THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY
> EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF
> MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL
> THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
> SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
> PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
> INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
> STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF
> THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## Wikidata

BAFTA, Golden Globe and Primetime Emmy award statements, National Film Registry and
Criterion Collection membership, and the identifier joins used for several lists come
from [Wikidata](https://www.wikidata.org/), whose data is published under
[CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/).

## Sight & Sound 2022 poll entries

Title and year data derived from
[samMint/Sight-and-Sound-Film-Data](https://github.com/samMint/Sight-and-Sound-Film-Data),
used under the MIT License, Copyright (c) 2023 samMint. The poll itself is conducted by
*Sight and Sound* magazine (BFI); only the identifiers of member films are embedded.

## IMDb Top 250

Membership of the IMDb Top 250 chart is extracted as bare IMDb title identifiers — no
ranks, ratings, or descriptions — from the chart's structured-data markup. IMDb is a
trademark of IMDb.com, Inc. The chart as a compilation remains IMDb's; this dataset
records only which titles were on it on the snapshot date.

## They Shoot Pictures, Don't They?

The TSPDT "1,000 Greatest Films" is compiled by Bill Georgaris at
[theyshootpictures.com](https://www.theyshootpictures.com/). Membership is embedded as
bare IMDb identifiers (no ranks), joined from TMDb identifiers collated by
[li4alex/tspdt-tmdb](https://github.com/li4alex/tspdt-tmdb) (no licence declared;
treated as an uncopyrightable collection of facts) through Wikidata. If you reuse this
data, credit the site.

## AFI 100 Years…100 Movies and BFI Top 100 British films

List membership parsed from the corresponding English Wikipedia articles
([CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/)); only film titles —
facts — are read, and they are embedded as IMDb identifiers resolved through Wikidata.
The lists themselves are published by the American Film Institute and the British Film
Institute respectively.

## Country name dictionary

`Data/countries.json.gz` is generated from [Unicode CLDR](https://cldr.unicode.org/)
data, used under the [Unicode License](https://www.unicode.org/license.txt).

## Collection artwork

The artwork set under `assets/thumbnails/` has its own attributions — the Noto font family
(SIL OFL 1.1) for the language posters and library tiles, and the supplied country posters,
whose provenance is still to be recorded — documented in
[assets/thumbnails/README.md](assets/thumbnails/README.md). None of it ships inside the
plugin.
