// Nodekeeper — gera ícones/logos (H4 · Anel de nós) e os temas XAML a partir de tokens.json.
// Grava SÓ dentro de design/nodekeeper/ (não toca em src/ nem em icons/ da raiz).
// Uso (na raiz do projeto): node design/nodekeeper/gerar.mjs
// Depois, para PNGs e .ico: powershell -File design/nodekeeper/render-icones.ps1
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const tokens = JSON.parse(readFileSync(join(here, 'tokens.json'), 'utf8'));
const icons = join(here, 'icons');
const render = join(here, 'icons', '_render');
const xamlDir = join(here, 'xaml');
for (const d of [icons, join(icons, 'app'), render, xamlDir]) mkdirSync(d, { recursive: true });

// ---------- fundo do ícone (gradiente de três cores) ----------
const ai = tokens.gradient.appIcon;
const tileDefs = (id) =>
  `<linearGradient id="${id}L" x1="0.33" y1="0" x2="0.67" y2="1"><stop offset="0" stop-color="${ai.base.from}"/><stop offset="1" stop-color="${ai.base.to}"/></linearGradient>` +
  ai.blobs.map((b, i) => `<radialGradient id="${id}R${i}" cx="${b.center[0]}" cy="${b.center[1]}" r="${b.radius}"><stop offset="0" stop-color="${b.color}"/><stop offset="1" stop-color="${b.color}" stop-opacity="0"/></radialGradient>`).join('');
const tileRects = (id, x, y, w, rx) =>
  [`url(#${id}L)`, ...ai.blobs.map((_, i) => `url(#${id}R${i})`)]
    .map((f) => `<rect x="${x}" y="${y}" width="${w}" height="${w}" rx="${rx}" fill="${f}"/>`).join('');

// ---------- símbolo H4 (grade 48) ----------
const hex = 'M24 8 37.86 16V32L24 40 10.14 32V16z';
const vertices = [[24, 8], [10.14, 16], [10.14, 32], [24, 40], [37.86, 32]]; // o 6.º (37.86,16) é o nó aceso
const symbolFull =
  `<path d="${hex}" stroke="#FFFFFF" stroke-opacity="0.5" stroke-width="2.4"/>` +
  `<path d="M24 24 35.5 17.3" stroke="#A08CFF" stroke-width="1.8" stroke-dasharray="1.4 2.6"/>` +
  `<circle cx="24" cy="24" r="3.2" fill="#FFFFFF" fill-opacity="0.92"/>` +
  vertices.map(([x, y]) => `<circle cx="${x}" cy="${y}" r="2.6" fill="#FFFFFF"/>`).join('') +
  `<circle cx="37.86" cy="16" r="7" fill="#A08CFF" fill-opacity="0.25"/><circle cx="37.86" cy="16" r="4" fill="#A08CFF"/>`;
const symbol48 = // 48 px e logotipo: sem tracejado e sem halo
  `<path d="${hex}" stroke="#FFFFFF" stroke-opacity="0.55" stroke-width="3"/>` +
  `<circle cx="24" cy="24" r="3.4" fill="#FFFFFF" fill-opacity="0.92"/>` +
  vertices.map(([x, y]) => `<circle cx="${x}" cy="${y}" r="3" fill="#FFFFFF"/>`).join('') +
  `<circle cx="37.86" cy="16" r="4.6" fill="#A08CFF"/>`;
const symbol32 = // 24–32 px: contorno + nó aceso + centro
  `<path d="${hex}" stroke="#FFFFFF" stroke-opacity="0.85" stroke-width="4"/><circle cx="37.86" cy="16" r="6" fill="#A08CFF"/><circle cx="24" cy="24" r="4" fill="#FFFFFF"/>`;
const symbol16 = // 16–20 px: contorno + nó aceso
  `<path d="M24 7 38.7 15.5V32.5L24 41 9.3 32.5V15.5z" stroke="#FFFFFF" stroke-width="5.5"/><circle cx="37" cy="16" r="8" fill="#A08CFF"/>`;

const g = (inner, t = '') => `<g fill="none" stroke-linecap="round" stroke-linejoin="round"${t ? ` transform="${t}"` : ''}>${inner}</g>`;

const app = `<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
<defs>${tileDefs('nk')}</defs>
${tileRects('nk', 8, 8, 240, 54)}
<rect x="8.75" y="8.75" width="238.5" height="238.5" rx="53.25" fill="none" stroke="#FFFFFF" stroke-opacity="0.16" stroke-width="1.5"/>
${g(symbolFull, 'translate(22.4 22.4) scale(4.4)')}
</svg>
`;
const app48 = `<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48">
<defs>${tileDefs('nk48')}</defs>
${tileRects('nk48', 1, 1, 46, 10.5)}
${g(symbol48, 'translate(3.4 3.4) scale(0.858)')}
</svg>
`;
const app32 = `<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48">
<defs>${tileDefs('nk32')}</defs>
${tileRects('nk32', 0.5, 0.5, 47, 10.5)}
${g(symbol32, 'translate(1.5 1.5) scale(0.9375)')}
</svg>
`;
const app16 = `<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48">
<defs>${tileDefs('nk16')}</defs>
${tileRects('nk16', 0, 0, 48, 11)}
${g(symbol16)}
</svg>
`;
writeFileSync(join(icons, 'app.svg'), app);
writeFileSync(join(icons, 'app', 'app-48.svg'), app48);
writeFileSync(join(icons, 'app', 'app-small-32.svg'), app32);
writeFileSync(join(icons, 'app', 'app-small-16.svg'), app16);

// ---------- logotipo (símbolo + "nodekeeper") ----------
const logo = (id, color) => `<svg xmlns="http://www.w3.org/2000/svg" width="182" height="40" viewBox="0 0 182 40">
<defs>${tileDefs(id)}</defs>
${tileRects(id, 0, 0, 40, 9)}
${g(symbol48, 'translate(2 2) scale(0.75)')}
<text x="50" y="27.5" font-family="Manrope, 'Segoe UI Variable Display', 'Segoe UI', sans-serif" font-size="24" font-weight="700" letter-spacing="-0.84" fill="${color}">nodekeeper</text>
</svg>
`;
writeFileSync(join(icons, 'logo.svg'), logo('nkLogo', '#ECE9F2'));
writeFileSync(join(icons, 'logo-light.svg'), logo('nkLogoL', '#1C1A22'));

// ---------- bandeja (uma cor, grade 24) ----------
writeFileSync(join(icons, 'app-tray.svg'),
  `<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 4 18.93 8V16L12 20 5.07 16V8z"/><circle cx="18.93" cy="8" r="3" fill="currentColor" stroke="none"/><circle cx="12" cy="12" r="2" fill="currentColor" stroke="none"/></svg>\n`);

// ---------- páginas para o render headless (PNG 256 de cada variante) ----------
const page = (svg) => `<!doctype html><html><head><style>html,body{margin:0;background:transparent}svg{display:block;width:256px;height:256px}</style></head><body>${svg}</body></html>`;
writeFileSync(join(render, 'r-full.html'), page(app));
writeFileSync(join(render, 'r-48.html'), page(app48));
writeFileSync(join(render, 'r-32.html'), page(app32));
writeFileSync(join(render, 'r-16.html'), page(app16));

// ---------- temas XAML ----------
const groups = [
  ['Janela', ['App.']], ['Cartões', ['Card.']], ['Texto', ['Text.']], ['Destaque', ['Accent']],
  ['Etiquetas', ['Badge.']], ['Estados', ['Status.']], ['Barras', ['Bar.']],
  ['Controles', ['Button.', 'Input.', 'Dialog.', 'Overlay.']], ['Widget', ['Widget.']],
];
const hex2 = (n) => Math.round(n * 255).toString(16).toUpperCase().padStart(2, '0');
const W = 1320, H = 860; // janela típica: raios das manchas viram frações dela

for (const [theme, file] of [['dark', 'Dark.xaml'], ['light', 'Light.xaml']]) {
  const t = tokens[theme];
  const used = new Set();
  let body = '';
  for (const [title, prefixes] of groups) {
    const keys = Object.keys(t).filter((k) => !used.has(k) && prefixes.some((p) => k.startsWith(p)));
    if (!keys.length) continue;
    body += `\n    <!-- ${title} -->\n`;
    for (const k of keys) { used.add(k); body += `    <SolidColorBrush x:Key="${k}" Color="${t[k]}"/>\n`; }
  }
  const gr = tokens.gradient[theme];
  body += `\n    <!-- Fundo em gradiente: empilhe App.Background + App.Glow1..3 (ver LEIA-ME) -->\n`;
  gr.blobs.forEach((b, i) => {
    const rgb = b.color.slice(1);
    const [cx, cy] = b.center;
    body += `    <RadialGradientBrush x:Key="App.Glow${i + 1}" Center="${cx},${cy}" GradientOrigin="${cx},${cy}" RadiusX="${(b.radius[0] / W).toFixed(3)}" RadiusY="${(b.radius[1] / H).toFixed(3)}">` +
      `<GradientStop Color="#${hex2(b.alpha)}${rgb}" Offset="0"/><GradientStop Color="#00${rgb}" Offset="0.6"/></RadialGradientBrush>  <!-- ${b.name} -->\n`;
  });
  const xaml = `<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- Nodekeeper · tema ${theme === 'dark' ? 'escuro' : 'claro'} · gerado de design/nodekeeper/tokens.json (não editar à mão) -->
${body}</ResourceDictionary>
`;
  writeFileSync(join(xamlDir, file), xaml, 'utf8');
  console.log(`${file}: ${Object.keys(t).length} pincéis + ${gr.blobs.length} manchas`);
}
console.log('ícones: app.svg, app/app-48|small-32|small-16.svg, logo.svg, logo-light.svg, app-tray.svg');
