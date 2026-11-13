# Hardware Watch — plano das novas funções

Situação em 07/11/2026: etapas 01 a 03 prontas (inventário, coletor como serviço com API segura, painel e widget).
A identidade visual Hardware Watch já está aplicada no painel (versão 0.4.0).

Este plano cobre os cinco pedidos novos, mais o instalador que já estava previsto. Para cada um: o que dá para
fazer, como, o que fica de fora e quanto trabalho dá. A ordem sugerida está no fim.

---

## 1. Histórico (atual, mínimo, máximo, tempo de medição)

**Dá para fazer: sim, por completo.**

Hoje o painel guarda só os últimos 2 minutos, na memória, e perde tudo ao fechar. O histórico de verdade tem que
ficar no **coletor**: ele roda 24 h como serviço, então o histórico existe mesmo com o painel fechado, e o NAS guarda
o dele.

**Como:**
- O coletor grava num banco SQLite em `%ProgramData%\HwMon\historico.db`, em três resoluções:

  | Resolução | Guarda por | Tamanho estimado* |
  |---|---|---|
  | 10 s (média, mín., máx.) | 48 horas | ~35 MB |
  | 1 min | 30 dias | ~60 MB |
  | 1 hora | 2 anos | ~25 MB |

  \* Para ~150 métricas. O ganho é que um ano de dados ocupa menos que uma foto RAW, e os HDDs do NAS não são
  acordados porque o banco fica no SSD do sistema.
- Uma API nova, `/v1/history?metric=…&from=…&to=…`, escolhe a resolução sozinha pelo período pedido.
- No painel, clicar num cartão abre a página do componente com:
  - um gráfico grande;
  - os períodos **5 min · 1 h · 24 h · 7 dias · 30 dias · 1 ano**;
  - **Atual / Mínimo / Média / Máximo** do período, com data e hora do pico;
  - "medindo desde…" e o tempo ligado.
- Contadores que só crescem: horas ligado por disco e **GB transferidos por dia, mês e ano**, separando internet e
  rede local.
- Exportar para CSV.

**Fica de fora:** dados de antes da instalação desta versão. O histórico começa no dia em que o coletor novo entrar.

**Trabalho:** grande (coletor + API + página nova). É a base para os alertas, por isso entra cedo.

---

## 2. Iniciar com o Windows (widget por padrão)

**Dá para fazer: sim, é simples.**

- Uma página **Configurações** no painel com:
  - "Iniciar com o Windows" (ligado por padrão);
  - "Ao iniciar, abrir: só o widget / painel e widget / nada (só ícone na bandeja)";
  - widget sempre no topo;
  - tema (seguir o Windows, claro ou escuro).
- O registro é por usuário: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Não precisa de administrador.
- **Ícone na bandeja** (perto do relógio), com o `app-tray` da identidade. O menu tem:
  - abrir o painel;
  - mostrar ou ocultar o widget;
  - trocar a máquina do widget;
  - sair.

  Fechar o painel deixa o programa na bandeja em vez de encerrar.
- Só uma cópia aberta por vez: abrir de novo traz a janela que já existe para a frente.

**Trabalho:** pequeno. Só mexe no painel; não precisa reinstalar o coletor.

---

## 3. Resumo do sistema (estilo CPU-Z)

**Dá para fazer: sim, a maior parte.** O coletor lê tudo uma vez ao iniciar (e quando algo muda) e o painel mostra
numa página **Sistema** de cada máquina, com botão para copiar ou exportar em texto.

| Grupo | Informações | Fonte |
|---|---|---|
| Placa-mãe e BIOS | fabricante, modelo, revisão, versão e data da BIOS, Secure Boot, TPM, modo UEFI | WMI (SMBIOS) |
| **12** | ✅ **Corewise** em tudo o que aparece (0.12.0) e ✅ **interface em inglês** (0.13.0: Configurações › Idioma, padrão = idioma do Windows). |
| Memória | por pente: slot, fabricante, part number, capacidade, tipo (DDR4/DDR5), velocidade nominal e configurada, canais | WMI (SMBIOS) |
| Placa de vídeo | nome, fabricante da placa, VRAM, versão e data do driver, versão da VBIOS*, PCIe atual e máximo (ex.: 4.0 x16)* | DXGI, WMI, SetupAPI, NVML* |
| Discos | modelo, firmware, serial, interface (SATA 6 Gb/s, NVMe PCIe 4.0 x4), capacidade, horas ligado, SMART completo | já coletado + IOCTL |
| Rede | placa, driver, MAC, velocidade do link, IP, gateway, DNS | WMI/NetAdapter |
| Sistema | edição do Windows, build, data de instalação, tempo ligado, nome do PC | WMI |
| Monitores | fabricante, modelo, resolução, tamanho, ano de fabricação | EDID via WMI |
| Extras | dispositivos USB, áudio, plano de energia, HVCI | SetupAPI/WMI |

\* VBIOS e PCIe da NVIDIA vêm da biblioteca da própria NVIDIA (NVML), que já vem com o driver. Para AMD, uma parte vem
do LibreHardwareMonitor.

**Fica de fora:** os **timings da memória** (CL, tRCD…). Eles exigem ler o chip SPD do pente por um barramento que
o Windows bloqueia. O CPU-Z faz isso com um driver próprio, e o PawnIO talvez permita no futuro. Por enquanto
aparecem só a velocidade e o tipo.

**Trabalho:** médio. Envolve muita fonte de dados, mas cada uma é simples.

---

## 4. Benchmarks

**Dá para fazer: sim.** Quem roda o teste é o coletor da máquina testada, então dá para testar o disco do NAS
sentado na workstation. Todo teste pede confirmação, mostra o que vai fazer e guarda o resultado para comparar depois.

### 4a. Disco (estilo CrystalDiskMark)
- Os mesmos 4 testes de leitura e escrita do CrystalDiskMark, para comparar direto:
  - **SEQ1M Q8T1** e **SEQ1M Q1T1** (sequencial);
  - **RND4K Q32T1** e **RND4K Q1T1** (aleatório).
- O arquivo de teste é de 1 GB por padrão (configurável), escrito sem cache do Windows e com dados aleatórios, e é
  apagado no fim.
- Extra útil para NAS: **escrita contínua longa**. Mostra a queda de velocidade quando acaba o cache rápido do SSD,
  ou a diferença entre o começo e o fim de um HDD.
- Cuidados embutidos:
  - aviso de que o teste grava no disco (desgaste mínimo, mas existe);
  - recusa se houver pouco espaço;
  - não testa enquanto o disco está em uso pesado.

### 4b. Rede entre dois PCs (estilo iperf)
- Os dois coletores conversam diretamente:
  - **velocidade TCP** nos dois sentidos, com 1 e com 4 conexões;
  - **latência e variação** (ping/jitter);
  - **perda de pacotes**.
- Mostra se o link está no que deveria (ex.: "940 Mbit/s de 1 Gbit/s possíveis = cabo e placa OK").
- Opcional: **teste de cópia real** por compartilhamento de rede (SMB), para ver a velocidade que o Explorer vai ter.
- Opcional: **velocidade da internet**. Usaria um servidor externo, como o da Cloudflare, então só roda se você pedir.

### 4c. Sugestões de outros testes
| Teste | Para quê |
|---|---|
| **CPU** um núcleo e todos os núcleos (compressão, criptografia, matemática) | comparar as duas máquinas e ver se um núcleo está mais lento |
| **Estabilidade/temperatura**: carga total por 10–30 min registrando temperatura, clock e consumo | ver se o cooler dá conta e se há **thermal throttling** (queda de clock por calor) |
| **Memória**: velocidade de leitura/cópia e latência | conferir se o perfil XMP/EXPO está funcionando |
| **Saúde do HDD**: leitura completa da superfície, só leitura | achar setores lentos antes de o disco falhar (bom para os discos do NAS) |
| **Placa de vídeo** | possível, mas pesado de fazer bem. Fica para depois; os jogos e o 3DMark já cobrem |

**Trabalho:** disco médio, rede médio, CPU/memória/estabilidade pequeno a médio cada.

---

### 4d. CPU, memória, GPU e estabilidade (escolhidos em 08/11/2026)

Todos rodam **no coletor** da máquina (como o de disco), então dá para testar o NAS ou um nó da render farm pelo
painel de outro PC. Resultados guardados para comparar ao longo do tempo e entre máquinas.

| Teste | Como | Mostra |
|---|---|---|
| **12** | ✅ **Corewise** em tudo o que aparece (0.12.0) e ✅ **interface em inglês** (0.13.0: Configurações › Idioma, padrão = idioma do Windows). |
| **Memória** | Blocos bem maiores que o cache L3 (ex.: 1 GB): leitura, escrita e cópia com instruções vetoriais em várias threads; **latência** por "pointer chasing" (lista ligada embaralhada em 256 MB). | GB/s de leitura, escrita e cópia; latência em ns (estilo AIDA64); ao lado, a velocidade e o perfil da RAM (EXPO/XMP) do resumo do sistema. |
| **12** | ✅ **Corewise** em tudo o que aparece (0.12.0) e ✅ **interface em inglês** (0.13.0: Configurações › Idioma, padrão = idioma do Windows). |
| **Estabilidade** | Estresse de CPU (todas as threads com as cargas do teste de CPU, conferindo os resultados a cada rodada) e opcionalmente GPU, por 10/30/60 min. | Gráfico de temperatura, clock e consumo; **throttling** (clock caindo com temperatura alta), **erros de cálculo** (resultado diferente = instabilidade/overclock), temperatura máxima. Botão de parar sempre visível; para sozinho se passar de um limite de temperatura. |

**Trabalho:** grande (GPU é a parte mais pesada, por precisar de DirectX 12). Ordem sugerida: CPU → memória →
estabilidade → GPU.

## 5. Bateria de headsets, mouses e Bluetooth

**Dá para fazer: em parte. Depende do aparelho.** São três casos:

| Tipo de aparelho | Dá? | Como |
|---|---|---|
| **Bluetooth** (fones, mouses, teclados, controles) que mostram a bateria nas Configurações do Windows | **Sim** | O Windows já guarda o nível; o programa lê a mesma informação |
| **Bluetooth** que **não** mostram a bateria no Windows | Às vezes | Alguns têm o "Battery Service" padrão e dá para perguntar direto |
| **Dongle USB próprio da marca** (Logitech Lightspeed, Razer HyperSpeed, Corsair Slipstream, SteelSeries, HyperX) | **Depende da marca** | Cada marca tem um protocolo próprio. A Logitech é bem documentada (HID++); as outras foram mapeadas por projetos abertos (ex.: HeadsetControl) e têm que ser implementadas uma a uma |
| **Controle Xbox** | Sim | API do próprio Windows |
| **Bateria de notebook** | Sim, e com bônus | Nível, carregando ou não, e **saúde da bateria** (capacidade atual contra a original) |

Os aparelhos aparecem como cartões **Periféricos** com nível, ícone e aviso de bateria baixa, e podem ir para o
widget. Um detalhe: eles ficam ligados no PC onde estão pareados, então quem lê é o coletor daquela máquina.

**Preciso saber:** quais headsets, mouses e outros aparelhos você usa, com marca, modelo e se é Bluetooth ou dongle.
Com a lista eu digo quais funcionam e priorizo.

**Trabalho:** pequeno para Bluetooth padrão e bateria de notebook; médio por marca de dongle.

---

## 6. Instalador e descoberta automática (já previsto)

- Um instalador único, sem PowerShell, com três opções:
  - **Completo** (painel + widget + coletor): a workstation.
  - **Só coletor** (serviço, sem nada na tela): NAS, servidores, **nós de render farm**. Pode ser instalado em
    silêncio (`/silencioso /papel "Render 01"`) para espalhar em várias máquinas de uma vez, e o código de
    pareamento pode vir de um arquivo, para parear todas a partir do painel sem ir em cada uma.
  - **Só painel** (sem coletor): um notebook que só acompanha as outras máquinas.
  - Dá para trocar depois (adicionar o painel num PC que tinha só o coletor) sem perder histórico e pareamento.
- Para muitas máquinas (render farm), o painel ganha uma vista em grade compacta: uma linha por máquina com CPU,
  GPU, temperatura e se está renderizando, ordenável e com filtro.
- O coletor aparece sozinho na rede (mDNS). O painel mostra "Encontrado: NAS" e o código de pareamento aparece numa
  janelinha no próprio NAS, sem linha de comando.
- **Atualização** sem reinstalar à mão. Isso pesa porque o histórico, o resumo do sistema e os periféricos mudam o
  coletor, e cada mudança hoje exige reinstalar nos dois PCs pelo PowerShell.
- Opcional: assinatura digital do instalador, para o SmartScreen não reclamar. Tem custo anual.

**Trabalho:** médio.

---

## Andamento (08/11/2026)

| Etapa | Conteúdo | Situação |
|---|---|---|
| 04 | Iniciar com o Windows, bandeja, Configurações | ✅ pronta |
| 05 | Resumo do sistema | ✅ pronta |
| 06 | Histórico | ✅ pronta |
| 07 | Periféricos e baterias (G733, MX Master 3S, K400 Plus, M275) | ✅ pronta |
| 07.1 | Ajustes rápidos (renomear, placa-mãe, G733, transparência, Wi-Fi 7) | ✅ pronta |
| 07.2 | Widget 2.0 (categorias, estilos, tempo de carga) | ✅ pronta |
| 07.3 | Painel 2.0 (Sensores, arrastar, visão geral, Processos, Energia, sites, % das ventoinhas) | ✅ pronta |
| 08 | Benchmarks de disco (estilo CrystalDiskMark) e rede entre PCs (estilo iperf) | ✅ pronta (CPU, memória, estabilidade e internet ficam para depois) |
| 09 | Modo jogo: FPS/1 % low/frametime por ETW (sem injeção) + overlay | ✅ pronta e validada |
| 10 | Histórico de jogos (sessões com FPS e sensores) e aba Histórico com o uso consolidado | ✅ pronta (falta validar a gravação com o coletor 0.10 instalado) |
| 10.1 | Filtro de jogos pela GPU, benchmark de disco ao vivo, alerta amarelo não sobe | ✅ pronta (falta validar com o coletor 0.10.1 instalado) |
| 11 | Benchmarks de CPU, memória, GPU e estabilidade (ver 4d) | ✅ pronta (falta validar no serviço: temperaturas e GPU na sessão 0) |
| **12** | ✅ **Corewise** em tudo o que aparece (0.12.0) e ✅ **interface em inglês** (0.13.0: Configurações › Idioma, padrão = idioma do Windows). |

---

## 7. Ajustes pedidos em 08/11/2026: o que dá para fazer

| # | Pedido | Dá? | Como / observação |
|---|---|---|---|
| 1 | Nome da máquina vindo do Windows, com opção de renomear | ✅ | O pareamento já sugere o nome do PC; falta renomear no painel (clicar no nome). O apelido fica só neste PC. |
| 2 | Aba de sensores consolidada em tempo real (estilo HWMonitor), exportar TXT ou imagem | ✅ | Item "Sensores" na barra lateral, abaixo das máquinas: **árvore com todas as máquinas** → componentes → sensores, colunas Atual · Mín · Máx **desde que o painel abriu** (com botão "zerar"). Exporta **TXT** e **PNG**. |
| 3 | Widget em duas versões: limpo (atual) e visual com ícones | ✅ | Opção "Estilo" no widget: Compacto ou Visual (ícone da categoria, valor maior). |
| 4 | Widget separado por categorias, menos denso | ✅ | Blocos CPU · Memória · GPU · Armazenamento · Rede · Periféricos, com espaço entre eles e título discreto. |
| 5 | Atalho para abrir o programa na pasta do projeto | ✅ feito | `Hardware Watch.lnk` na raiz do projeto, apontando para `publish\painel\HwMon.exe`. |
| 6 | Bateria: tempo que falta para carregar | ⚠️ estimado | Os aparelhos não informam isso. Dá para **estimar** pela velocidade de carga dos últimos minutos ("cheia em ~40 min"), e o mesmo para a descarga ("~9 h restantes"). Precisa de alguns minutos de dados para aparecer. |
| 7 | Widget mostrar o tempo de carga só enquanto carrega | ✅ | Opção no widget; a linha só aparece quando o aparelho está carregando. Depende do item 6. |
| 8 | Site do fabricante no resumo do sistema | ⚠️ por tabela | O hardware não traz o endereço. Uma tabela com os fabricantes conhecidos (MSI, ASUS, Gigabyte, ASRock, AMD, Intel, NVIDIA, Corsair, Seagate, WD, Samsung, Logitech…) abre a página de suporte ou de busca do modelo. Fabricante fora da tabela fica sem link. |
| 9 | Abaixo do nome da workstation, mostrar o modelo da placa-mãe em vez de "Micro-Star International Co., Ltd. MS-7E51" | ✅ | O coletor passa a mandar a placa-mãe ("MSI MAG X870 TOMAHAWK WIFI"). |
| 10 | Potência em W do que está carregando nas portas USB | ❌ / parcial | O Windows **não mede** a corrente das portas USB, e a placa-mãe também não informa. Dá para mostrar o **máximo declarado** por cada aparelho USB (ex.: "até 0,5 A"), o que não é o consumo real. Um cartão **Energia** com o que é medido de verdade (CPU e GPU em W) é possível; o consumo total do PC só com fonte "inteligente" (ex.: Corsair com USB) ou tomada inteligente. |
| 11 | Arrastar os cartões para reorganizar | ✅ | A ordem fica salva por máquina. Cartão em alerta **continua subindo** enquanto o alerta durar. |
| **12** | ✅ **Corewise** em tudo o que aparece (0.12.0) e ✅ **interface em inglês** (0.13.0: Configurações › Idioma, padrão = idioma do Windows). |
| **13** | ✅ **Instalador** (0.12.0) e ✅ **0.13.0**: pareamento por aceite ("Permitir?" no outro PC), código pelo painel, atalhos configuráveis, widget por PC, limpar histórico, remover resultados. ⏳ Falta: atualização automática; assinatura digital só no lançamento no Reddit. |
| 14 | Personalizar quais blocos aparecem na visão geral e a ordem | ✅ | "Personalizar" também na visão geral: escolher e ordenar os blocos de cada máquina. |
| **12** | ✅ **Corewise** em tudo o que aparece (0.12.0) e ✅ **interface em inglês** (0.13.0: Configurações › Idioma, padrão = idioma do Windows). |
| 16 | Controle de transparência do widget | ✅ | Controle deslizante nas opções do widget (o ajuste já existia nas configurações, sem tela). |
| 17 | Bateria do G733 diferente do G HUB (80 % × 96 %) e demora para mostrar desligado | ✅ corrigível | **Causa:** eu usava uma curva genérica de lítio. Na mesma tensão (3,99 V), a curva calibrada para headsets G (projeto HeadsetControl) dá **94 %**, perto dos 96 % do G HUB. **Desligado:** o headset era lido a cada 60 s e a última leitura ficava valendo por 3 min. Passa a ser lido a cada 10 s, e "desligado" aparece na hora. |

---

## Ordem sugerida (revista em 08/11/2026)

| Etapa | Conteúdo | Muda o coletor? | Tamanho |
|---|---|---|---|
| **07.1** | **Ajustes rápidos**: renomear máquina (1), placa-mãe no subtítulo (9), bateria do G733 e "desligado" rápido (17), transparência do widget (16), Wi-Fi 7 duplicado | Sim | Pequeno |
| **07.2** | **Widget 2.0**: categorias com espaço (4), estilos compacto e visual (3), tempo estimado de carga e de bateria (6, 7) | Sim (estimativa) | Médio |
| **12** | ✅ **Corewise** em tudo o que aparece (0.12.0) e ✅ **interface em inglês** (0.13.0: Configurações › Idioma, padrão = idioma do Windows). |
| **08** | Benchmarks (disco e rede primeiro) | Sim | Grande |
| **09** | **Modo jogo**: FPS, frametime, 1 % low e GPU num overlay (15) | Sim | Grande |
| **10** | **Histórico de jogos e uso consolidado** (pedido depois do modo jogo) | Sim | Grande |
| **11** | Instalador, descoberta e atualização | — | Médio |

Depois disso vêm os **alertas**, que já estavam na lista de ideias e reaproveitam o histórico: aviso no Windows
quando um disco esquentar, o espaço acabar, a saúde mudar, uma bateria ficar baixa ou uma máquina sair do ar.

## Decisões já tomadas
- Aba Sensores: uma árvore com todas as máquinas; Mín/Máx desde que o painel abriu (como o HWMonitor).
- Arrastar cartões: a sua ordem vale; só cartão em alerta **crítico** sobe (amarelo fica onde está, desde a 0.10.1).
- Modo jogo depois dos benchmarks.
- Histórico: 10 s por 48 h, 1 min por 30 dias, 1 h por 2 anos.
- Jogos: só vira sessão com uso real da GPU 3D pelo processo (≥ 10 %, ou ≥ 3 % em biblioteca de jogos).
- **Nome do produto: Corewise** (09/11/2026), substituindo "Hardware Watch". Logo novo sendo feito na thread de
  identidade visual. Por dentro o código continua `HwMon`; o instalador muda o que aparece e migra dados e pareamentos.
- **Distribuição:** gratuito, com doação opcional (Ko-fi / GitHub Sponsors / Buy Me a Coffee), lançado em inglês
  pelo Reddit (r/pcmasterrace, r/buildapc, r/overclocking), com site próprio para download.

## Próximas etapas (revistas em 09/11/2026)

| Etapa | Conteúdo |
|---|---|
| **12** | ✅ **Corewise** em tudo o que aparece (0.12.0) e ✅ **interface em inglês** (0.13.0: Configurações › Idioma, padrão = idioma do Windows). |
| **13** | ✅ **Instalador e pareamento** (0.12.0–0.13.3): pareamento por aceite, atalhos, widget por PC, Wake-on-LAN, periféricos Bluetooth/Xbox, correções da auditoria, testes automáticos. ⏳ Falta: atualização automática. Assinatura: SignPath recusou por enquanto (11/2026); tentar de novo quando o projeto tiver histórico público. |
| **14** | **Site**: página estática (download, recursos, capturas, doação, "o que o serviço faz e por que precisa de administrador", link do VirusTotal) e código no GitHub com licença definida (respeitando MPL 2.0 do LibreHardwareMonitor e a licença do PawnIO). |
| **15** | **Ranking de benchmarks**: envio opcional pelo painel (pontuação, modelos de CPU/GPU/placa/RAM/disco, versão do teste, sensores durante o teste; sem nome do PC, série ou IP), API pequena (Cloudflare Workers + D1 ou Supabase), página de ranking com **mediana por modelo**, filtro de valores impossíveis, limite de envios, "verificado" quando os sensores batem, versão do teste congelada (CPU "v1" = workstation 1000). |
| depois | FPS de jogos da comunidade ("vai rodar no meu PC?"), exige saber resolução e qualidade; alertas; botão "exportar diagnóstico". |

Lançamento no Reddit só depois do ranking ter alguns resultados.

## 8. Em avaliação: agente Linux em Docker (ideia de 13/11/2026)

**Situação:** plano futuro, a avaliar depois do lançamento no Reddit. Só vale se houver pedido (o post do r/homelab
pergunta "Linux agent?"; contar as respostas).

**O que seria:** um "Corewise Agent" para servidores e NAS **Linux** (Unraid, TrueNAS SCALE, Proxmox, Ubuntu),
distribuído como contêiner Docker, que fala o **mesmo protocolo** do serviço do Windows (HTTPS, pareamento por
aceite/código, token). O painel do Windows mostra a máquina Linux como qualquer outra, sem mudar o painel.

**O que não vai para o Docker:** o painel e o widget (aplicativos de janela WPF) e o serviço do Windows (sensores
por driver de kernel PawnIO, WMI, ETW para FPS, HID; contêiner isola justamente o hardware).

**Coleta no Linux (o que muda):**
- Temperaturas, ventoinhas e tensões: `/sys/class/hwmon` (o mesmo que o `lm-sensors` lê).
- CPU, memória, rede, volumes: `/proc` e `/sys`.
- Discos: SMART com `smartctl` (smartmontools) dentro da imagem; sem acordar HDDs em repouso (`-n standby`).
- GPU: `nvidia-smi` (NVIDIA); AMD/Intel por `/sys/class/drm` quando disponível.
- Sem overlay de FPS nem periféricos (não fazem sentido num servidor).
- O contêiner precisa de acesso a `/sys`, `/dev` dos discos e, para SMART, privilégio (`--privileged` ou
  `--device` + `CAP_SYS_RAWIO`); documentar as duas formas.

**Reaproveitamento:** .NET roda em Linux; API, pareamento, certificado, histórico (SQLite), benchmarks de rede e
de disco são quase todos portáveis. Separar a camada de coleta (interfaces) da parte Windows antes de começar.

**Entregas:**
1. Separar coletores Windows atrás de interfaces (sem mudar comportamento; testes atuais passando).
2. Coletores Linux + execução como serviço comum (systemd) para testar fora do Docker.
3. Imagem Docker (multi-arch amd64/arm64), `docker-compose.yml` de exemplo, modelos para Unraid e TrueNAS.
4. Pareamento sem painel local: código de pareamento no log do contêiner e por comando (`docker exec … parear`).
5. Testar em 2–3 distribuições e num NAS real; documentação.

**Esforço:** grande (algumas semanas). **Combina com:** painel web num segundo contêiner (ver o painel pelo celular
ou de outro sistema) — juntos formariam uma "versão homelab".

## Em aberto
- Benchmark de internet: quer, mesmo usando um servidor externo?
- Conferir marca e domínio de "Corewise" (corewise.com / .app / .io) antes do lançamento.
- Licença do código aberto (MIT, GPL…).
