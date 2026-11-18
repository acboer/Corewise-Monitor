// Gera src/HwMon.App/Theme/FluentAccent.{Dark,Light}.xaml a partir de Resources/Theme/{Dark,Light}.xaml do WPF Fluent
// (github.com/dotnet/wpf). Uso: node fluent-accent.mjs <tema do WPF.xaml> <saída.xaml> <Dark|Light>
// Depois: trocar {StaticResource ControlStrokeColorDefault} pela cor do tema (#12FFFFFF escuro, #0F000000 claro).
import fs from 'node:fs';
const [,, src, out, theme] = process.argv;
const ramp = { SystemAccentColor:'#FF7B66E8', SystemAccentColorLight1:'#FF8F7BF5', SystemAccentColorLight2:'#FFA08CFF', SystemAccentColorLight3:'#FFC2B5FF', SystemAccentColorDark1:'#FF5B47D6', SystemAccentColorDark2:'#FF4936B3', SystemAccentColorDark3:'#FF36278A' };
const xml = fs.readFileSync(src, 'utf8');
const items = [];
// Elementos de uma linha (<SolidColorBrush .../>) e blocos (<LinearGradientBrush ...>...</LinearGradientBrush>) com a cor de destaque.
const re = /<(SolidColorBrush|LinearGradientBrush|Color)\b[^>]*x:Key="([^"]+)"[^>]*?(\/>|>[\s\S]*?<\/\1>)/g;
for (const m of xml.matchAll(re)) {
  if (!/SystemAccentColor/.test(m[0])) continue;
  items.push(m[0].replace(/\{StaticResource (SystemAccentColor[A-Za-z0-9]*)\}/g, (_, k) => ramp[k] ?? (()=>{throw new Error(k)})()));
}
const head = `<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- Nodekeeper · cor de destaque dos controles Fluent (tema ${theme}). Os controles do WPF (caixa de seleção, opção,
         controle deslizante, foco das caixas de texto…) pintam com a cor de destaque do Windows; estes são os mesmos
         pincéis do tema Fluent do WPF (github.com/dotnet/wpf, licença MIT) com o violeta da identidade no lugar.
         Gerado de Resources/Theme/${theme}.xaml; App.ApplyAccent copia para o topo de Application.Resources. -->
`;
fs.writeFileSync(out, '﻿' + head + items.map(i => '    ' + i.trim()).join('\n') + '\n</ResourceDictionary>\n');
console.log(out, items.length);
