#!/usr/bin/env node
// Writes the pixl icons the README and the GitHub releases use as standalone SVG files, from the
// Blazor sources in Indx.Systm.Blazor/Icons. Generated, never hand-edited, so there is one source for each icon.
//
//   node docs/icons/generate.mjs
//
// Two things a README image needs that the components do not:
//  - A colour of its own. GitHub shows these through <img>, where currentColor has no text to
//    inherit and falls back to black, invisible in the dark theme. It is one mid grey that reads
//    on both backgrounds, not a prefers-color-scheme switch: inside an image that media query
//    follows the operating system, not the page, so a GitHub or VS Code theme set apart from the
//    OS drew the icons in the background's own colour.
//  - A fixed pixel scale. Every icon is drawn at the same size per grid unit, and crispEdges keeps
//    the pixels square rather than smoothed.

import { readFileSync, writeFileSync, readdirSync, unlinkSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const SOURCE = join(HERE, '../../Indx.Systm.Blazor/Icons');

// The icons the README uses. Add a name here, then run this.
const ICONS = [
  'IndxLogo', 'InternetBrowser', 'UserGroup', 'Api', 'DynamicJsonField', 'ShadowIndexing', 'Hibernate', 'Mcp',
  'Login', 'Boost', 'Synonym', 'Bell', 'Terminal',
  // The 2.0 GitHub release, which links them by their raw.githubusercontent.com address.
  'FuzzySearch', 'HybridSearch', 'Eye', 'Key', 'Speedometer', 'Github',
];

const SCALE = 3;                 // CSS pixels per grid unit: a 7x5 icon becomes 21x15
const COLOR = '#848d97';        // GitHub's muted foreground: 3.4:1 on white, 5.6:1 on its dark background

const kebab = s => s.replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase();

for (const f of readdirSync(HERE).filter(f => f.endsWith('.svg'))) unlinkSync(join(HERE, f));

for (const name of ICONS) {
  const razor = readFileSync(join(SOURCE, `${name}.razor`), 'utf8');
  const svg = razor.match(/<svg\b[^>]*>([\s\S]*?)<\/svg>/);
  const viewBox = svg?.[0].match(/viewBox="0 0 ([\d.]+) ([\d.]+)"/);
  if (!svg || !viewBox) throw new Error(`${name}: no <svg> with a viewBox in the source`);

  const body = svg[1].trim().replaceAll('"@Color"', `"${COLOR}"`);
  if (body.includes('@')) throw new Error(`${name}: Razor expression left in the markup`);

  const [w, h] = [Number(viewBox[1]), Number(viewBox[2])];
  const out = `<svg xmlns="http://www.w3.org/2000/svg" width="${w * SCALE}" height="${h * SCALE}" ` +
    `viewBox="0 0 ${w} ${h}" fill="none" shape-rendering="crispEdges">\n` +
    `${body}\n</svg>\n`;

  writeFileSync(join(HERE, `${kebab(name)}.svg`), out);
  console.log(`  ${kebab(name)}.svg`);
}
