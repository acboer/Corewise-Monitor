# Corewise — identidade visual

**Nome:** Corewise (substitui "Hardware Watch" e o provisório "HwMon"; o código interno continua `HwMon`).

## Decisões

**Conceito (logo C3 · Órbita).** Um núcleo ciano aceso — o *core* — dentro de um anel aberto em forma de C, com um nó
na abertura: a outra máquina na rede local. Sem chip nem pulso literais, para funcionar como marca de produto em
qualquer tamanho. O ícone do aplicativo segue a linguagem dos ícones do macOS (quadrado bem arredondado, grafite com
luz no topo, núcleo com brilho). Em 24–32 px somem o halo, o brilho e o tracejado; em 16–20 px fica só o anel com o
núcleo. O ciano do núcleo é a única cor viva do logo e é o destaque da interface inteira.

**Nome escrito.** "corewise" em minúsculas, Space Grotesk 600 (SIL OFL 1.1), espaçamento −0,03 em. A interface
continua em Inter.

**Paleta.** Escuro é o tema principal, com grafites levemente azulados. O destaque é ciano (`#4FD8EB` no escuro,
`#0B7C8E` no claro, mais escuro para passar 4,5:1 sobre o branco). Ele se distingue do azul do próprio Windows e fica
longe das cores de estado. Verde, âmbar e vermelho só aparecem quando há algo a dizer: o normal é neutro. Todos os
textos cinza passam no contraste de 4,5:1.

**Interface.** Em vez de seções por tipo, a página da máquina é uma grade única (colunas de no mínimo 232 px,
preenchimento denso) que ocupa toda a janela. CPU, GPU e qualquer componente em atenção ou crítico ocupam duas
colunas e sobem para o topo; os outros preenchem os vãos. O número importante é grande (28 px, semibold, algarismos
tabulares) e o resto é discreto. Leitura antiga aparece a 50 % de opacidade com a idade do dado.

**Ícones.** Grade de 24 px com 2 px de margem, traço de 1,5 px com pontas e junções arredondadas, sempre em
`currentColor`. Os estados usam formas diferentes além da cor (círculo, triângulo, octógono), então continuam
distinguíveis para quem não percebe a cor.

**Fonte.** Inter, gratuita para embutir (SIL OFL 1.1), com algarismos tabulares para os valores não "pularem"
quando mudam. Segoe UI Variable fica como reserva.

## Arquivos

| Arquivo | Conteúdo |
|---|---|
| `tokens.json` | Cores `{ "dark": {...}, "light": {...} }` com os nomes do briefing e tokens novos. Transparências em `#AARRGGBB`. |
| `tipografia.json` | Família, escala tipográfica e medidas de layout (grade, cartões, widget). |
| `../icons/` | 63 ícones de uma cor, `app.svg`, `app.ico`, PNGs, `logo.svg` e `logo-light.svg`. |
| `gerar-xaml.mjs` | Regrava `src/HwMon.App/Theme/Dark.xaml` e `Light.xaml` a partir de `tokens.json`: `node design/gerar-xaml.mjs`. Edite as cores no JSON, não no XAML. |
| `xaml-antigo/` | Cópia dos temas anteriores (paleta azul provisória), caso queira voltar. |

### Tokens novos (além dos do briefing)

`App.Divider`, `Card.Inset` (blocos dentro do cartão da visão geral), `Accent.Foreground` (texto sobre botão
principal), `Accent.SoftText`, `Badge.Foreground`, `Status.CautionSoft` / `CautionText`, `Status.CriticalSoft` /
`CriticalText` (faixas e recados), `Button.*`, `Input.*`, `Dialog.Background`, `Overlay.Scrim`, `Widget.BarTrack`,
`Widget.Text.Secondary`.

## Pendências

- O nome em `logo.svg` / `logo-light.svg` ainda é texto (Space Grotesk). Converta em curvas para ficar idêntico em
  qualquer máquina.
- O app ainda mostra "HwMon" e o ícone antigo: o nome e o `app.ico` entram na etapa de renomear (etapa 12).
- Mockups: canvas "Hardware Watch — Identidade" (telas escuras e claras, cartões, widget e diálogos).
