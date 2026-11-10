// Gera Theme/Dark.xaml e Theme/Light.xaml a partir de design/tokens.json.
// Uso (na raiz do projeto): node design/gerar-xaml.mjs
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const tokens = JSON.parse(readFileSync(join(root, 'design', 'tokens.json'), 'utf8'));
const out = join(root, 'src', 'HwMon.App', 'Theme');

const groups = [
  ['Janela', ['App.']],
  ['Cartões', ['Card.']],
  ['Texto', ['Text.']],
  ['Destaque', ['Accent']],
  ['Etiquetas', ['Badge.']],
  ['Estados', ['Status.']],
  ['Barras', ['Bar.']],
  ['Controles', ['Button.', 'Input.', 'Dialog.', 'Overlay.']],
  ['Widget', ['Widget.']],
];

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
  const rest = Object.keys(t).filter((k) => !used.has(k));
  if (rest.length) { body += '\n    <!-- Outros -->\n'; for (const k of rest) body += `    <SolidColorBrush x:Key="${k}" Color="${t[k]}"/>\n`; }
  const xaml = `<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- Corewise · tema ${theme === 'dark' ? 'escuro' : 'claro'} · gerado de design/tokens.json (não editar à mão) -->
${body}</ResourceDictionary>
`;
  writeFileSync(join(out, file), xaml, 'utf8');
  console.log(`${file}: ${Object.keys(t).length} pincéis`);
}
