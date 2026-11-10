# Briefing: interface e ícones do HwMon

Este documento descreve um aplicativo de monitoramento de hardware para Windows 11 que já existe e funciona.
O pedido é **redesenhar a identidade visual e criar o conjunto de ícones**, mantendo a estrutura de telas descrita
aqui. O resultado será implementado em WPF (.NET 10), então as entregas precisam seguir o formato da seção 9.

---

## 1. O produto

**HwMon** (nome provisório; pode sugerir outro) é um monitor de hardware para uma pequena rede doméstica/estúdio:

- Uma **workstation** de computação gráfica (AMD Ryzen 9 9950X, NVIDIA RTX 5070, 128 GB DDR5, SSD NVMe + 3 HDDs)
  usada para renders em 3ds Max.
- Um **NAS** montado num PC com Windows 11 (Intel Core i5-14400, 32 GB DDR4, 1 SSD + 3 HDDs).
- Outros PCs podem ser adicionados.

Cada PC roda um serviço coletor. O aplicativo mostra os dados de todos em dois formatos:
1. **Painel**: janela principal com visão geral e uma página detalhada por máquina.
2. **Widget**: janela pequena para deixar na área de trabalho, opcionalmente sempre no topo.

**Referência de estilo pedida pelo usuário:** a organização visual de sistemas de NAS como **TrueNAS SCALE, ZimaOS
e QNAP QTS**: cartões com um valor principal em destaque, gráfico curto, detalhes secundários, estados por cor,
visual limpo e moderno.

**Público:** o próprio usuário (arquiteto/visualização 3D), que trabalha em português do Brasil. Toda a interface é
em **pt-BR**.

---

## 2. Restrições técnicas

- **Plataforma:** Windows 11, WPF. A interface precisa parecer nativa do Windows 11 (cantos arredondados, Fluent),
  mas com personalidade própria de "painel de NAS".
- **Tema claro e escuro:** o app segue o tema do Windows. Toda cor deve ter as duas versões.
- **Fonte:** hoje usa **Segoe UI Variable** (Text e Display). Pode sugerir outra se for gratuita para embutir em
  aplicativo desktop (ex.: Inter); diga qual e por quê.
- **Ícones pequenos:** vários ícones aparecem a **12–16 px** (widget, etiquetas de cartão, bandeja do Windows).
  Precisam ser legíveis nesse tamanho.
- **Ícones de uma cor:** o app pinta os ícones conforme tema e estado (normal/atenção/crítico). Use traço/preenchimento
  em `currentColor`, sem cores fixas, exceto no ícone do aplicativo.
- **Widget** tem fundo próprio com leve transparência e sombra; deve funcionar sobre qualquer papel de parede.

---

## 3. Paleta e tokens atuais (ponto de partida, pode ser substituída)

| Token | Escuro | Claro | Uso |
|---|---|---|---|
| `App.Background` | `#14161B` | `#F3F4F7` | Fundo da janela |
| `App.Sidebar` | `#1A1D23` | `#E9ECF1` | Barra lateral |
| `App.SidebarSelected` | `#262B33` | `#DCE1E8` | Item selecionado/hover |
| `Card.Background` | `#1F232A` | `#FFFFFF` | Cartões |
| `Card.BackgroundHover` | `#242931` | `#F8F9FB` | Cartão sob o mouse |
| `Card.Border` | `#2B3039` | `#E1E5EA` | Borda dos cartões |
| `Text.Primary` | `#E8EAED` | `#1D2127` | Texto principal |
| `Text.Secondary` | `#A0A8B4` | `#5A636E` | Rótulos |
| `Text.Tertiary` | `#6B7380` | `#8C949E` | Texto apagado |
| `Accent` | `#4AA3FF` | `#0A6CDB` | Destaque, gráficos, barras |
| `Accent.Soft` | `#1E3550` | `#DCEBFC` | Fundos de aviso informativo |
| `Badge.Background` | `#283140` | `#E6EEF8` | Etiqueta de tipo no cartão |
| `Status.Ok` | `#3CCB7F` | `#1F8A47` | Conectado, saudável |
| `Status.Caution` | `#F5B13D` | `#B7791F` | Atenção, desconectado |
| `Status.Critical` | `#F2555A` | `#D1242F` | Crítico, falha |
| `Bar.Track` | `#2E343E` | `#E4E7EC` | Fundo das barras de ocupação |
| `Widget.Background` | `#1A1D23` | `#FBFCFD` | Fundo do widget |
| `Widget.Border` | `#33FFFFFF` | `#22000000` | Borda do widget |

Escala tipográfica atual: título de página 26 semibold · valor principal do cartão 30 semibold · título do cartão
14 semibold · rótulos 12–13 regular · seção 13 semibold.

**Mantenha os mesmos nomes de token** (pode mudar os valores e acrescentar novos). O código já usa esses nomes.

---

## 4. Telas

### 4.1 Janela principal (painel)
Layout: **barra lateral fixa (≈236 px) à esquerda + conteúdo à direita**. Janela típica 1320×860, mínimo 900×600.

**Barra lateral, de cima para baixo**
- Logotipo/nome do app + subtítulo "Monitor de workstation e NAS".
- Lista de navegação: **Visão geral** e, abaixo, **uma entrada por máquina** (nome + "atualizado agora" ou
  "desconectado desde 18:33:55", com indicador de status).
- Rodapé: botões **Adicionar máquina** e **Mostrar/Ocultar widget**.

### 4.2 Visão geral
Título "Visão geral" + subtítulo. **Um cartão grande por máquina** (≈440 px), em grade que quebra linha:
- Cabeçalho: status, nome, etiqueta de papel ("Workstation", "NAS"), "atualizado agora".
- 4 blocos em grade 2×2, cada um com título, valor grande, barra de ocupação (quando faz sentido) e linha secundária.
  - **Workstation:** CPU (8 % · 57 °C · 4,83 GHz), GPU (0 % · 52 °C · RTX 5070), Memória (34 % · 42,8 GB de 125 GB),
    Rede (↓ 0,31 Mbit/s · ↑ 0,18 Mbit/s · hoje 1,23 GB).
  - **NAS:** Espaço (4,90 TB livres de 7,51 TB em 4 volumes), Discos (4 saudáveis · até 40 °C), Rede, CPU.
- Sem máquinas: cartão vazio "Nenhuma máquina pareada" com botão **Adicionar máquina**.

### 4.3 Página da máquina
- Cabeçalho: nome grande + etiqueta de papel; linha com modelo da placa e Windows; status + "atualizado agora".
  À direita: botões **Personalizar** (alternância) e **Remover**.
- Faixa de aviso quando desconectada: "desconectado desde 18:49:39 — mostrando os últimos dados recebidos".
- Faixa informativa no modo Personalizar.
- **Seções** com título, cada uma com cartões de 300 px em grade fluida (seções ficam lado a lado se couberem).
  Na workstation: Processador, Gráficos, Memória, Armazenamento, Rede, Ventoinhas. No NAS, Armazenamento vem primeiro.

**Anatomia do cartão de componente (300 px)**
1. Etiqueta de tipo (ícone) + título (ex.: "AMD Ryzen 9 9950X") + caixa de seleção (só no modo Personalizar).
2. Subtítulo cinza (ex.: "16 núcleos · 32 threads", "HDD · SATA · 4001 GB").
3. **Valor principal grande** + legenda ("5 %" "de uso"; "0 %" "tempo ativo"; "↓ 1,7 Mbit/s" "recebendo").
4. **Gráfico curto** (últimos 2 minutos, linha com preenchimento suave, sem eixos), ≈36 px de altura.
5. Linhas rótulo/valor (ex.: Clock 4,40 GHz · Temperatura 62 °C · Consumo 64 W).
6. Nos discos: **volumes** com nome (D: CONCLUIDOS), "270 GB livres de 1,00 TB" e barra de ocupação.
7. Ao clicar: detalhes extras (núcleo mais rápido, hot spot, horas ligado...).
- A borda e o valor mudam de cor em atenção/crítico.
- Valores antigos (ex.: HDD parado, máquina desconectada) aparecem **esmaecidos**.

### 4.4 Widget
Janela sem moldura, ≈270 px de largura, altura conforme conteúdo, cantos arredondados, sombra, arrastável.
- Cabeçalho: status, nome da máquina, setas **‹ ›** (trocar máquina), **alfinete** (sempre no topo, destacado
  quando ativo), **engrenagem** (escolher métricas), **X** (fechar).
- Linha "atualizado agora".
- Linhas: rótulo à esquerda, valor à direita, barra fina abaixo quando é percentual/temperatura.
  Ex.: CPU · uso 4 % · CPU · temperatura 56 °C · RAM 34 % · GPU · uso 2 % · GPU · temperatura 50 °C · Rede ↓ 1,72 Mbit/s · Rede ↑ 0,17 Mbit/s.
  No NAS: volumes (D:, E:, F:) com % ocupado e temperatura dos discos.
- Menu de botão direito: Abrir painel, Escolher métricas, Sempre no topo, Fechar widget.

### 4.5 Diálogos
- **Adicionar máquina:** campo "Nome ou IP do PC" + **Procurar** → mostra o PC encontrado e a **impressão digital do
  certificado** (ex.: `AE33-BCE1-18E3-D402`) para o usuário conferir → campos "Código de 6 dígitos" e "Nome no
  painel" → **Parear**. Mensagem de erro em vermelho.
- **Métricas do widget:** lista agrupada por componente, com caixas de seleção; botões Restaurar padrão, Cancelar,
  Salvar.
- **Remover máquina:** confirmação.

---

## 5. Lista completa de ícones

Use exatamente estes nomes de arquivo (o código vai referenciá-los). Grade de **24×24 px**, testados em 16 e 12 px.

### 5.1 Aplicativo
| Arquivo | Uso | Observações |
|---|---|---|
| `app.svg` | Ícone do programa (atalho, barra de tarefas, Alt+Tab, instalador) | **Colorido**, entregar também PNG 256/48/32/16 e `.ico` |
| `app-tray.svg` | Ícone da bandeja (perto do relógio) | Monocromático, legível em 16 px, claro e escuro |
| `logo.svg` | Logotipo na barra lateral (símbolo + nome) | Versões para fundo claro e escuro |

### 5.2 Navegação (barra lateral)
| Arquivo | Uso |
|---|---|
| `nav-overview.svg` | Visão geral |
| `machine-workstation.svg` | Máquina do tipo workstation |
| `machine-nas.svg` | Máquina do tipo NAS |
| `machine-generic.svg` | Outra máquina (papel não definido) |
| `action-add-machine.svg` | Adicionar máquina |
| `action-widget.svg` | Mostrar/ocultar widget |
| `nav-settings.svg` | Configurações (futuro) |

### 5.3 Seções e tipos de componente (cabeçalho de seção e etiqueta do cartão)
| Arquivo | Componente |
|---|---|
| `kind-cpu.svg` | Processador |
| `kind-gpu.svg` | Placa de vídeo dedicada |
| `kind-gpu-integrated.svg` | Vídeo integrado |
| `kind-ram.svg` | Memória |
| `kind-storage.svg` | Seção Armazenamento |
| `kind-ssd.svg` | SSD SATA |
| `kind-nvme.svg` | SSD NVMe |
| `kind-hdd.svg` | Disco rígido |
| `kind-disk-external.svg` | Disco USB/externo ou tipo desconhecido |
| `kind-volume.svg` | Volume/partição (D:, E:) |
| `kind-network.svg` | Seção Rede / adaptador cabeado |
| `kind-wifi.svg` | Adaptador Wi-Fi |
| `kind-network-virtual.svg` | Rede virtual/VPN |
| `kind-fan.svg` | Ventoinhas |

### 5.4 Blocos da visão geral
Reaproveitam `kind-cpu`, `kind-gpu`, `kind-ram`, `kind-network`; novos:
| Arquivo | Bloco |
|---|---|
| `tile-space.svg` | Espaço livre (NAS) |
| `tile-disk-health.svg` | Saúde dos discos (NAS) |

### 5.5 Métricas (opcionais, ícones bem pequenos ao lado dos rótulos)
| Arquivo | Métrica |
|---|---|
| `metric-usage.svg` | Uso / tempo ativo |
| `metric-clock.svg` | Clock (GHz) |
| `metric-temperature.svg` | Temperatura |
| `metric-power.svg` | Consumo (W) |
| `metric-vram.svg` | VRAM |
| `metric-read.svg` | Leitura (MB/s) |
| `metric-write.svg` | Escrita (MB/s) |
| `metric-download.svg` | Recebendo (↓) |
| `metric-upload.svg` | Enviando (↑) |
| `metric-today.svg` | Total do dia |
| `metric-health.svg` | Saúde do disco |
| `metric-wear.svg` | Desgaste do SSD |
| `metric-hours.svg` | Horas ligado |
| `metric-rpm.svg` | Rotação da ventoinha |

### 5.6 Estados
| Arquivo | Estado |
|---|---|
| `status-connected.svg` | Conectado |
| `status-connecting.svg` | Conectando |
| `status-disconnected.svg` | Desconectado (tentando de novo) |
| `status-failed.svg` | Falha: acesso revogado ou certificado mudou |
| `status-ok.svg` | Saudável |
| `status-caution.svg` | Atenção |
| `status-critical.svg` | Crítico |
| `status-stale.svg` | Leitura antiga (sem atualização recente) |
| `status-secure.svg` | Certificado conferido / conexão segura |

### 5.7 Ações
| Arquivo | Ação |
|---|---|
| `action-customize.svg` | Personalizar |
| `action-remove.svg` | Remover máquina |
| `action-previous.svg` | Máquina anterior (widget) |
| `action-next.svg` | Próxima máquina (widget) |
| `action-pin.svg` | Sempre no topo: ligado |
| `action-unpin.svg` | Sempre no topo: desligado |
| `action-metrics.svg` | Escolher métricas |
| `action-close.svg` | Fechar |
| `action-search.svg` | Procurar PC na rede |
| `action-expand.svg` / `action-collapse.svg` | Mostrar/ocultar detalhes do cartão |

### 5.8 Futuro (etapas seguintes; manter o mesmo estilo)
`feature-discovery.svg` (PC encontrado na rede), `feature-startup.svg` (iniciar com o Windows),
`feature-alerts.svg` (alertas), `feature-history.svg` (histórico 24 h), `feature-transfer.svg` (modo transferência:
rede e discos dos dois PCs juntos).

---

## 6. Diretrizes de ícone

- Grade 24×24 com 2 px de margem; traço de **1,5 px** com pontas e junções arredondadas (ou outro padrão, desde que
  único em todo o conjunto).
- `fill="none" stroke="currentColor"` (ou `fill="currentColor"` se o estilo for sólido). Nenhuma cor fixa, exceto
  no `app.svg`.
- SVG simples: apenas `path`, `circle`, `rect`, `line`, `polyline`. Sem filtros, máscaras, gradientes, textos ou
  imagens embutidas.
- Os pares precisam ser distinguíveis a 16 px: HDD × SSD × NVMe; GPU dedicada × integrada; pin × unpin.
- Evitar logotipos de marcas (AMD, NVIDIA, Intel, Seagate etc.).

---

## 7. O que o usuário quer da identidade visual

- Limpa e moderna, com a clareza dos painéis de NAS: **o número importante salta aos olhos**, o resto é discreto.
- Leitura rápida de estado: verde/amarelo/vermelho só onde há algo a dizer; o normal é neutro.
- Escuro como tema principal (uso noturno durante renders), claro igualmente bem resolvido.
- O widget deve ser discreto sobre a área de trabalho e legível de relance.

---

## 8. Perguntas que você pode responder na proposta

- Nome definitivo do aplicativo (se quiser propor alternativas a "HwMon").
- Cor de destaque (o azul atual é provisório).
- Se os cartões devem ter ícone + etiqueta de texto, ou só ícone.
- Densidade: tamanho do cartão (hoje 300 px) e do widget (hoje 270 px).

---

## 9. Entregas esperadas

1. **Ícones** em SVG com os nomes da seção 5, numa pasta `icons/`.
2. **Ícone do aplicativo**: `app.svg` + `app.ico` (16, 20, 24, 32, 48, 256) + PNGs.
3. **Tokens de cor** com os mesmos nomes da seção 3, em JSON:
   `{ "dark": { "App.Background": "#14161B", ... }, "light": { ... } }`.
4. **Escala tipográfica** (família, tamanhos, pesos para: título de página, título de seção, título de cartão,
   valor principal, rótulos, texto do widget).
5. **Mockups** (PNG ou HTML estático) das telas, em escuro e claro:
   visão geral · página da workstation · página do NAS · widget (workstation e NAS) · Adicionar máquina ·
   Métricas do widget · estado desconectado.
6. Um parágrafo curto explicando as decisões (conceito, paleta, estilo dos ícones).

Use os dados de exemplo das seções 4.2–4.4 nos mockups. Eles são reais e mostram tamanhos de texto típicos.
