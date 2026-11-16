# Nodekeeper — identidade visual

**Status: aplicada no app na versão 0.14.0 (2026-11-16).** Temas em `src/HwMon.App/Theme/`, ícones em `icons/`, fonte Manrope estática em `fonts/` (embutida no painel).

**Nome:** Nodekeeper, sempre em minúsculas no logotipo (`nodekeeper`). Substitui Corewise, que já existe como outro
produto com interface e cores parecidas. O código interno continua `HwMon`.

Mockups: no canvas de design "Hardware Watch — Identidade", fileira **"Nodekeeper — hexágonos e fundo em gradiente"**:
quadro *Nodekeeper · logos hexagonais H1–H4* (a escolhida é a H4) e telas *Gradiente · Visão geral*, *Gradiente ·
Workstation* e *Gradiente · Workstation (claro)*.

---

## Decisões

**Logo (H4 · Anel de nós).** Um hexágono com seis nós nos cantos — as máquinas da rede — e o guardião (*keeper*) no
centro. Um dos nós, no canto de cima à direita, está aceso em violeta e ligado ao centro por um tracejado: é a máquina
sendo vigiada agora. O ícone do app segue a linguagem dos ícones do macOS (quadrado bem arredondado, luz no topo,
sombra embaixo) sobre um fundo em gradiente de três cores. Simplificação por tamanho:

| Tamanho | O que aparece |
|---|---|
| 256 | anel, 6 nós, nó aceso com halo, tracejado até o centro |
| 48 | anel, 6 nós, nó aceso (sem halo e sem tracejado) |
| 24–32 | contorno grosso, nó aceso e centro |
| 16–20 | contorno grosso e nó aceso |
| bandeja | as mesmas formas da linha 24–32, numa cor só (`currentColor`) |

**Como se afasta do Corewise.** Eles: grafite esverdeado, ciano, anel redondo com pulso no logo, item ativo da barra
lateral numa pílula azul sólida, cartões opacos e títulos de seção com bolinha. Nós: fundo em gradiente violeta,
magenta e índigo; destaque violeta; hexágono sem pulso nem círculo; item ativo em vidro com tom violeta; cartões de
vidro translúcido numa grade que preenche a janela. **Evite reintroduzir ciano, pulsos ou anéis redondos.**

**Paleta "Íris".** Destaque violeta `#A08CFF` (escuro) / `#5B47D6` (claro, mais escuro para passar 4,5:1 no branco).
As cores de estado não mudaram (verde, âmbar, vermelho) e continuam sendo as únicas cores "de alerta" na tela.

**Fundo em gradiente.** Cor base + três manchas radiais grandes e suaves (valores em `tokens.json` → `gradient`):

| Tema | Base | Mancha 1 (canto sup. esq.) | Mancha 2 (canto inf. dir.) | Mancha 3 (topo à direita) |
|---|---|---|---|---|
| Escuro | `#0E0C14` | violeta `#7C5CFF` 34 % | magenta `#D64C8C` 22 % | índigo `#3C5ADC` 18 % |
| Claro | `#F4F2FA` | lavanda `#8C6EFF` 30 % | rosa `#FF8CB9` 32 % | azul `#78AAFF` 24 % |

**Vidro.** Barra lateral, cartões, botões e etiquetas são brancos (escuro) ou brancos/violetas (claro) com alfa baixo,
então o gradiente aparece por trás. Ex.: `Card.Background` = branco 4,5 % no escuro, branco 62 % no claro. Nos mockups
há desfoque (`backdrop-filter`); **no WPF não precisa** — o gradiente é liso, e cartões só translúcidos ficam iguais
na prática. Não use Mica/Acrylic do Windows na janela: ele substituiria o gradiente.

**Fonte.** Manrope (SIL OFL 1.1, gratuita para embutir), mais arredondada que a Inter do Corewise. Valores em 700 com
algarismos tabulares; rótulos em 500–600. A escala completa está em `tipografia.json`.

**Ícones de interface.** Os 63 ícones de uma cor de `icons/` (raiz) continuam valendo: são neutros e pintados por
`currentColor`. Só o `app-tray.svg` muda — use o desta pasta.

---

## Arquivos

| Arquivo | Conteúdo |
|---|---|
| `tokens.json` | Cores `dark`/`light` com os mesmos 35 nomes que o app já usa (transparências em `#AARRGGBB`) + `gradient` (fundo da janela e do ícone). |
| `tipografia.json` | Família, escala e medidas de layout (grade, cartões com raio 18, widget). |
| `icons/app.svg`, `icons/app.ico` | Ícone do app (gradiente) e .ico com 256, 48, 32, 24, 20, 16. |
| `icons/app/app-*.png` | PNGs de cada tamanho e as variantes SVG simplificadas (`app-48`, `app-small-32`, `app-small-16`). |
| `icons/logo.svg`, `icons/logo-light.svg` | Logotipo para fundo escuro e claro. O nome é texto em Manrope: converta em curvas antes de publicar. |
| `icons/app-tray.svg` | Ícone da bandeja, uma cor. |
| `xaml/Dark.xaml`, `xaml/Light.xaml` | Temas prontos (35 pincéis + `App.Glow1..3`), no mesmo formato de `src/HwMon.App/Theme/`. |
| `gerar.mjs` | Regera ícones/logos e os XAML a partir de `tokens.json`. |
| `render-icones.ps1` | Renderiza o ícone no Edge headless e monta PNGs + `app.ico`. |

Regerar tudo (na raiz do projeto):

```
node design/nodekeeper/gerar.mjs
powershell -ExecutionPolicy Bypass -File design/nodekeeper/render-icones.ps1
```

---

## Como aplicar no WPF (para a conversa de desenvolvimento)

1. **Temas.** Copie `xaml/Dark.xaml` e `xaml/Light.xaml` por cima de `src/HwMon.App/Theme/`. Todas as 35 chaves que o
   código já usa continuam existindo; a troca claro/escuro por caminho (`App.xaml.cs`) não muda. (Ou ajuste
   `design/gerar-xaml.mjs` para ler `design/nodekeeper/tokens.json`.)
2. **Fundo em gradiente.** Na janela principal, troque o `Background` único por camadas:

   ```xml
   <Grid>
       <Rectangle Fill="{DynamicResource App.Background}"/>
       <Rectangle Fill="{DynamicResource App.Glow1}"/>
       <Rectangle Fill="{DynamicResource App.Glow2}"/>
       <Rectangle Fill="{DynamicResource App.Glow3}"/>
       <!-- conteúdo: barra lateral + página, por cima -->
   </Grid>
   ```

   A barra lateral (`App.Sidebar`) e os cartões já são translúcidos, então o gradiente aparece por trás deles. Os raios
   das manchas estão calculados para 1320×860; em janelas maiores elas crescem junto (são relativas).
3. **Cartões.** Raio 18 (antes 14), borda `Card.Border` de 1 px; cartão de máquina raio 20.
4. **Item selecionado da barra lateral.** Fundo `App.SidebarSelected` (vidro violeta) + barra de 3 px à esquerda em
   `Accent`. Não usar pílula sólida.
5. **Fonte.** Embutir Manrope (variável) em `Resources/Fonts` e trocar `Font.Text`/`Font.Display` em `Styles.xaml`.
6. **Ícone e nome.** `app.ico` e `app-tray.svg` desta pasta; nome "Nodekeeper" e logotipo na barra lateral.
7. **Widget.** Usa `Widget.Background` (quase opaco) — continua legível sobre qualquer papel de parede; não recebe o
   gradiente.
