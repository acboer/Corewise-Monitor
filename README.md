# Corewise — monitor de hardware para vários PCs (antes "Hardware Watch")

Roteiro: [Documentação/Roteiro - Monitor de Workstation e NAS.pdf](Documentação/Roteiro%20-%20Monitor%20de%20Workstation%20e%20NAS.pdf) ·
próximas etapas: [Documentação/Plano - Novas funções.md](Documentação/Plano%20-%20Novas%20funções.md) ·
identidade visual: [design/LEIA-ME.md](design/LEIA-ME.md)

## Estrutura

| Pasta | O que é |
|---|---|
| `src/HwMon.Core` | Modelo de telemetria (`Reading` com valor, unidade, horário, estado e fonte) e coletores. Será reaproveitado pelo serviço. |
| `src/HwMon.Inventory` | **Primeiro ticket**: utilitário que inventaria os componentes, captura leituras e gera JSON + matriz de sensores. |
| `src/HwMon.Collector` | **Etapa 02**: serviço do Windows que coleta continuamente e publica a API HTTPS na LAN. |
| `src/HwMon.Client` | Biblioteca do cliente: PCs pareados, certificado fixado, stream ao vivo com reconexão. Será usada pelo painel e pelo widget. |
| `src/HwMon.Cli` | `hwmon.exe`: cliente de teste em linha de comando (parear, ver ao vivo). |
| `src/HwMon.App` | **Etapas 03–04**: `HwMon.exe`, painel, widget e ícone na bandeja (WPF, tema claro/escuro). Pacote em `publish\painel`. |
| `design/`, `icons/` | Identidade visual Hardware Watch: tokens de cor, tipografia, ícones SVG e scripts que geram os temas e os ícones do WPF. |

## Etapa 13.1: inglês, pareamento por aceite, atalhos e widgets por PC (0.13.0)

- **Interface em inglês**: Configurações › Idioma (padrão: o do Windows; vale ao reabrir o painel, botão "Reabrir
  agora"). O texto em português no código é a chave; as traduções ficam em `src/HwMon.Core/Idiomas/en.json`
  (`L.T("…")` no C#; no XAML os textos fixos são traduzidos sozinhos, e `{controls:T '…'}` onde isso não alcança).
  O serviço sempre fala português com os painéis e cada painel traduz, inclusive frases com números (modelos com
  `{0}`) e partes separadas por " · ". `--idioma en` força o idioma numa execução (testes).
- **Pareamento por aceite**: Adicionar máquina › Procurar › **Pedir acesso**; no outro PC o painel mostra "… quer
  acompanhar este PC" com Permitir/Recusar (vale 2 min; só o painel daquele PC responde, pela conexão local). Sem
  painel lá (só o serviço), continua o código, que agora também sai pelo painel (Configurações e menu da bandeja), sem
  administrador. API: `POST /v1/pair/ask`, `GET /v1/pair/ask/{id}`, `GET /v1/pair/pending` e
  `POST /v1/pair/pending/{id}?approve=` (locais), `POST /v1/pair/code` (local).
- **Atalhos configuráveis**: modo jogo (Ctrl+Shift+F10) e widget (Ctrl+Shift+F9), trocados em Configurações; avisa
  se outro programa já usa a combinação.
- **Widget**: um só (setas trocam de PC) ou um para cada PC, escolhendo quais PCs aparecem; o atalho mostra ou
  esconde todos.
- **Histórico**: "Limpar histórico…" (uso e sensores, jogos ou os dois; uma máquina ou todas).
- **Benchmarks**: X para remover um resultado da lista.
- **Jogos**: tempo jogado conta só o tempo com quadros (jogo esquecido aberto em segundo plano não conta; as sessões
  antigas são corrigidas uma vez); launchers de anticheat (`start_protected_game`) e acesso remoto (Área de Trabalho
  Remota, AnyDesk, Parsec…) não viram jogo, e o overlay não aparece na janela de acesso remoto.
## Etapas 12 e 13: Corewise e instalador (0.12.0)

- **Nome Corewise** em tudo o que aparece: janelas, barra lateral (logo C3 · Órbita + "corewise"), bandeja, widget,
  overlay, mensagens, ícone dos executáveis. Por dentro o código continua `HwMon`.
  - `HwMon.exe` → **`Corewise.exe`** (painel); `HwMon.Collector.exe` → **`Corewise.Service.exe`** (serviço).
  - Serviço `HwMonCollector` → **`Corewise`**; regra de firewall `Corewise`; "iniciar com o Windows" `HardwareWatch` → `Corewise`.
  - Dados: `%AppData%\HwMon` → **`%AppData%\Corewise`** e `%ProgramData%\HwMon` → **`%ProgramData%\Corewise`**, movidos
    sozinhos na primeira execução (máquinas pareadas, histórico, jogos, benchmarks e certificado continuam valendo).
- **Pareamento sem linha de comando**:
  - No mesmo PC (instalação completa), o painel pareia sozinho com o serviço local (pedido de 127.0.0.1 não precisa
    de código).
  - Em outro PC: **Iniciar › Corewise › Código de pareamento** (pede administrador e mostra código, nome e IPs do PC);
    no painel, **Adicionar máquina**.
- **Instalador** `publish\CorewiseSetup-0.12.0.exe` (Inno Setup 6, 83 MB; os dois executáveis somam ~280 MB):
  - Tipos: **Completo** (painel + serviço), **Só o serviço** (NAS, servidor, render farm), **Só o painel**.
  - Português ou inglês (pelo idioma do Windows), atalho no menu Iniciar (e na área de trabalho, opcional),
    desinstalador em Aplicativos instalados (os dados em `%ProgramData%\Corewise` ficam).
  - Atualizar = rodar o Setup novo por cima: para o serviço, fecha o painel, troca os arquivos e religa. Também
    remove a instalação antiga (serviço `HwMonCollector`, `C:\Program Files\HwMon`).
  - Silencioso (várias máquinas): `CorewiseSetup-0.12.0.exe /VERYSILENT /SUPPRESSMSGBOXES /TYPE=servico`.
  - **Sem assinatura digital**: o Windows mostra "O Windows protegeu o computador" → *Mais informações* →
    *Executar assim mesmo*.
- Gerar: `powershell -ExecutionPolicy Bypass -File instalador\gerar.ps1` (publica em `publish\corewise` e compila
  `instalador\Corewise.iss`). Precisa do Inno Setup 6 (`winget install JRSoftware.InnoSetup`).
- Os pacotes antigos (`publish\painel`, `publish\coletor`, `atualizar.cmd`) ficam obsoletos.

## Etapa 11: benchmarks de CPU, memória, GPU e estabilidade (coletor e painel 0.11.0)

Quatro cartões novos na aba **Benchmarks** de cada máquina, abaixo de Disco e Rede. Todos rodam **no coletor** da
máquina (dá para testar o NAS pelo painel da workstation), um teste pesado por vez (contando o de disco), com os
sensores gravados a cada segundo e os resultados em `%ProgramData%\HwMon\benchmarks-carga.json` (50 por tipo). Cada
resultado mostra a diferença para o teste anterior.

- **Processador** (~30 s): compressão Brotli, AES-256 + SHA-256, contas vetoriais (AVX) e ordenação, cada uma 3 s
  em 1 thread e 3 s em todas. Pontos = média geométrica de (operações/s ÷ referência) × 1000; a referência é a
  workstation (Ryzen 9 9950X) em 1 núcleo, calibrada em 08/11/2026: **~1000 em 1 núcleo, ~18.500 em todos
  (18,5×)**. Só compara as suas máquinas (não é a escala do Cinebench).
- **Memória** (~15 s, estilo AIDA64): bloco de 1 GB (256 MB com pouca RAM livre), uma thread por núcleo físico.
  Leitura vetorial, escrita e cópia com instruções *non-temporal* (direto para a RAM), latência por *pointer chasing*
  em 256 MB. Workstation: **leitura 62,5 · escrita 56,5 · cópia 30,4 GB/s (bytes copiados) · 105 ns** (a latência
  sai acima da do AIDA, que usa páginas grandes).
- **Placa de vídeo** (~15 s): DirectX 11 *compute shader* (Vortice), sem janela (funciona no serviço). FP32 por
  cadeias de FMA compiladas com IEEE estrito; banda da VRAM por cópia de 512 MB dentro da placa. Usa a placa com
  mais memória dedicada. Workstation (RTX 5070): **32,6 TFLOPS · 418 GB/s**.
- **Estabilidade** (5/10/30/60 min, opcionalmente com a GPU): todas as threads nas cargas da CPU, conferindo cada
  resultado com o de referência (diferente = **erro de cálculo**). Gráfico ao vivo de temperatura, clock e consumo
  de CPU e GPU; no fim, temperatura máxima, clock no início × fim (**throttling** se cair 5 % ou mais) e consumo
  máximo. Para sozinho com CPU ≥ 100 °C ou GPU ≥ 92 °C por 10 s; cancelar mostra o que rodou até ali.
- API: `POST /v1/bench/run` (`{kind: cpu|memoria|gpu|estabilidade, minutes, gpu}`), `GET /v1/bench/run/status?kind=`,
  `POST /v1/bench/run/cancel`, `GET /v1/bench/run/history[?kind=]`. Console: `HwMon.Collector.exe teste cpu|memoria|gpu`.

## Etapa 10.1: filtro de jogos, disco ao vivo e alertas amarelos (coletor e painel 0.10.1)

- **Filtro de jogos pela GPU**: além de desenhar 15+ FPS por 2 min, o processo precisa usar a **engine 3D da GPU**
  (contador "GPU Engine" do Windows, por processo): média ≥ 10 % nos últimos 2 min, ou ≥ 3 % se o `.exe` estiver
  numa biblioteca de jogos (Steam, Epic, Xbox, GOG…). Dropbox, Surfshark, Logi Options+ e G HUB medem 0 % (só
  redesenham a interface) e não viram mais sessão. Dois processos do mesmo jogo ao mesmo tempo (launcher + jogo):
  fica o que mais usa a GPU.
- **Limpeza única** ao instalar a 0.10.1: apaga as sessões já gravadas de programas fora das bibliotecas de jogos.
- **Benchmark de disco ao vivo**: a tabela aparece desde o início e cada célula mostra a velocidade enquanto mede
  ("medindo…"); os valores ficam conforme cada teste termina. `/v1/bench/disk/status` ganhou `partial`, `liveMBs`,
  `liveTest`, `liveWrite`.
- **Cartões em atenção (amarelo) não sobem mais** nem ficam largos: só mudam a cor e ficam onde você arrastou. Só o
  crítico (vermelho) sobe e ocupa duas colunas enquanto durar.

## Etapa 10: histórico de jogos e uso consolidado (coletor e painel 0.10.0)

- **Sessões de jogo gravadas pelo coletor** (mesmo com o painel fechado): a cada segundo o serviço pergunta ao leitor
  de quadros quem está desenhando. Programa fora da lista de "não jogos" (navegadores, players, Discord, launchers,
  Windows…) a **15+ FPS** vira candidato e passa a ter **todos** os quadros acumulados (histograma de frametime em
  passos de 0,1 ms); com **2 minutos** desenhando, vira sessão. A sessão acaba quando o processo fecha ou fica 2 min
  sem quadros; é regravada a cada minuto (queda de energia perde no máximo 1 min).
  - Por sessão: início/fim, tempo jogado, quadros, **FPS médio, 1 % low e 0,1 % low da partida inteira**, tempo de
    quadro médio e pior, engasgos (quadros acima do dobro da média), média/máximo de GPU (uso, °C, W, clock), VRAM,
    CPU (uso, °C, W) e RAM, **energia CPU + GPU (Wh)** e um ponto a cada 5 s para o gráfico.
  - **Nome do jogo**: pasta do jogo na Steam/Epic/Xbox/GOG/EA/Ubisoft/Riot/Rockstar, senão o nome do produto do
    `.exe`, senão o nome do `.exe`. Dá para **renomear** e **ignorar** um programa no painel.
  - Banco próprio: `%ProgramData%\HwMon\jogos.db` (sessões guardadas sem prazo; poucos KB cada). `GamesEnabled` no
    `config.json` desliga.
  - API: `GET /v1/games?from&to`, `GET /v1/games/sessions/{id}`, `DELETE /v1/games/sessions/{id}`,
    `POST /v1/games/update` (`{exe, name?, ignored?}`).
- **Uso consolidado** (`GET /v1/usage?from&to`): dia a dia, a partir do histórico (1 min até 30 dias, 1 h além):
  tempo ligada, uso e temperatura média/máxima de CPU e GPU (com o horário do pico), energia CPU + GPUs, tempo com
  carga ≥ 80 %, rede recebida/enviada, disco lido/gravado e tempo jogando.
- **Painel → Histórico** (abaixo de Sensores), períodos 7 dias / 30 dias / 12 meses / Tudo:
  - **Jogos**: "jogando agora", tempo jogado, sessões, jogos, mais jogado; tabela de jogos (clique filtra as sessões;
    botão direito renomeia ou ignora; "Programas ignorados" no fim para voltar a gravar); tabela de sessões.
  - **Sessão**: FPS médio, 1 % / 0,1 % low, engasgos, tempo jogado, energia; gráfico ao longo da partida (FPS com a
    faixa do 1 % low, uso/temperatura/consumo da GPU, uso/temperatura da CPU); sensores; **comparar com outra
    sessão** do mesmo jogo (ex.: antes e depois de atualizar o driver); excluir a sessão.
  - **Uso**: um cartão por máquina com tempo ligada, energia, jogando, carga pesada, CPU/GPU mais quente, rede e
    disco, e barras por dia (ou por mês em 12 meses/Tudo) com a métrica escolhida.
  - **Exportar CSV** em cada vista (sessões, pontos de uma sessão, uso por dia).
- `HwMon.exe --abrir historico` (ou `sensores`) abre direto na página.
- A sessão ETW do leitor de quadros passa a ficar sempre ligada no serviço (o gravador consulta a cada segundo).
- Correção: nas etiquetas dos gráficos (histórico de um cartão), trocar a métrica às vezes recarregava a anterior.

## Etapa 09: modo jogo (coletor e painel 0.9.0)

- **FPS sem injetar nada no jogo**: o coletor (SISTEMA) liga, só enquanto alguém consulta, uma sessão ETW com os
  provedores do Windows **DXGI** (DX10/11/12; Vulkan/OpenGL quando o driver apresenta por DXGI, padrão da NVIDIA)
  e **Direct3D 9**, e conta os eventos Present de cada processo — o mesmo método do PresentMon. Frametime = intervalo
  entre Presents; **FPS** = média do último segundo; **1 % low / 0,1 % low** = percentis 99 / 99,9 do frametime nos
  últimos 10 s. Sem anticheat, sem DLL no jogo. A sessão para sozinha após 60 s sem consultas.
  API: `GET /v1/frames[?pid=]`. Console (administrador): `HwMon.Collector.exe quadros`.
- **Overlay** (painel): pequeno, sempre no topo, **não recebe cliques** (vão para o jogo) e fica fora do Alt+Tab.
  Mostra o programa, FPS, 1 % low (amarelo/vermelho se cair muito abaixo da média), frametime, gráfico de
  frametime, GPU (uso · °C · W · clock), VRAM, CPU e RAM. Sempre escuro, com fundo translúcido.
  - **Automático**: aparece quando um programa em tela cheia (que cobre o monitor) está na frente e desenha 15+ FPS;
    navegadores e players de vídeo ficam de fora. **Ctrl+Shift+F10** mostra/esconde a qualquer momento (também em
    janela); esconder pelo atalho vale até trocar de programa.
  - **Esconde o widget** enquanto o jogo está na frente (sem mudar a preferência dele) e volta depois.
  - Configurações → Modo jogo: automático / só pelo atalho / desligado, esconder widget, gráfico, canto da tela.
  - Usa o coletor **deste** PC (precisa estar pareado aqui). Tela cheia exclusiva antiga pode cobrir o overlay.

## Etapa 08: benchmarks de disco e rede (coletor e painel 0.8.0)

Aba **Benchmarks** na página de cada máquina (ao lado de Painel e Sistema).

- **Disco** (roda no coletor da máquina, estilo CrystalDiskMark): SEQ1M Q8T1, SEQ1M Q1T1, RND4K Q32T1, RND4K Q1T1,
  leitura e escrita, em MB/s (1 MB = 1.000.000 bytes, como o CDM); IOPS e latência nos aleatórios.
  - Arquivo de teste `HardwareWatch-benchmark.tmp` na raiz do volume (256 MB, 1 GB ou 4 GB), cheio de dados
    aleatórios e acessado **sem o cache do Windows** (FILE_FLAG_NO_BUFFERING), com N pedidos assíncronos na fila.
    Apagado no fim (também ao cancelar/falhar). Recusa se faltar espaço (tamanho + 1 GB). 5 s por medição.
  - Confirmação antes, progresso ao vivo, cancelar; resultados guardados no coletor (`benchmarks.json`) e listados
    como "testes anteriores". API: `POST /v1/bench/disk`, `GET /v1/bench/disk/status`, `/cancel`, `/history`.
  - Console: `HwMon.Collector.exe benchmark <letra> [MB] [segundos]`.
- **Rede** (entre o PC do painel e a máquina): latência (20 pedidos, mediana e variação), depois 10 s recebendo e
  10 s enviando com 4 conexões em paralelo, ignorando os 2 s iniciais. Usa a própria porta HTTPS do coletor (sem
  regra nova no firewall). Compara com a velocidade do link do adaptador deste PC ("X % de 1 Gbit/s") e diz se está
  no limite ou abaixo. Histórico em `%AppData%\HwMon\benchmarks-rede.json`. Na página do próprio PC aparece um aviso
  (não há rede para medir). Console: `hwmon rede <máquina>`.
- Medido na workstation (E: ST3000DM008, 256 MB): SEQ1M Q8T1 ≈ 179 / 180 MB/s, RND4K Q1T1 ≈ 0,7 / 1,4 MB/s
  (172 / 349 IOPS). Rede em loopback passou de 34 Gbit/s (o método não limita uma rede de 1–10 Gbit).

## Etapa 07.3: painel 2.0 (coletor e painel 0.7.4)

- **Aba Sensores** (barra lateral, abaixo das máquinas): árvore com todas as máquinas → componentes → sensores, ao
  vivo, colunas Atual · Mínimo · Máximo desde que o painel abriu ("Zerar mín./máx." recomeça). **Exportar TXT**
  (colunas alinhadas) e **Exportar imagem** (PNG da lista inteira, com o fundo do tema).
- **Arrastar cartões** para mudar a ordem (salva por máquina). Cartão em atenção/crítico continua subindo enquanto
  durar o alerta; CPU e GPU não ficam mais presos no topo. "Ordem padrão" no modo Personalizar.
- **Visão geral personalizável**: botão Personalizar → marcar os blocos de cada máquina e mudar a ordem (setas);
  "Padrão" volta ao original. Blocos novos: **Energia** (CPU + GPU em W) e **Periféricos** (a menor bateria).
- **Cartão Processos** (largura toda, abaixo dos cartões): top 5 de CPU, RAM e disco, agrupados pelo nome do
  programa, a cada 3 s, só com a página da máquina aberta. Coletor: `GET /v1/processes`. Desmarcável no Personalizar.
- **Cartão Energia**: soma do consumo medido (CPU + GPUs) com gráfico; placa-mãe, discos e ventoinhas não têm sensor.
- **Ventoinhas**: RPM e a **% de controle (PWM)** de cada saída ("2.469 RPM · 55 %"), também na ventoinha da GPU.
  O nome do perfil da BIOS não chega ao Windows.
- **Resumo do sistema**: link "Abrir página do fabricante ↗" (placa-mãe, CPU, memória, placa de vídeo, discos,
  rede, monitores), por uma tabela de fabricantes conhecidos; para MSI, ASUS, Gigabyte, Intel, Corsair e Dell já
  abre a busca pelo modelo.
- **Periféricos em repouso**: o coletor lembra os aparelhos já vistos (`%ProgramData%\HwMon\perifericos.json`);
  depois de reiniciar, um mouse/teclado dormindo aparece com a última leitura em vez de sumir até a próxima busca.

## Etapa 07.2: widget 2.0 (coletor e painel 0.7.3)

- **Blocos por categoria**: CPU · Memória · GPU · Armazenamento · Rede · Periféricos · Ventoinhas, nessa ordem,
  com espaço entre eles; dentro do bloco, a ordem escolhida. Rótulos curtos ("Uso", "Temperatura"); quando o
  bloco tem vários componentes (discos, periféricos), o nome entra no rótulo ("G733", "K400 Plus").
- **Dois estilos** (botão direito no widget → Estilo, ou Configurações): **Compacto** (título pequeno, mais
  denso) e **Visual** (ícone da categoria, valores maiores, barras mais grossas).
- **Tempo estimado de bateria** (coletor 0.7.3): os aparelhos não informam; o coletor estima pela tendência da %
  (reta sobre as leituras): **para carregar** com 8+ min carregando (janela de 30 min) e **restante** com 30+ min
  na bateria (janela de 6 h). Recomeça ao pôr/tirar da base. Teclados a pilha (K400) mudam em degraus e quase
  nunca têm estimativa.
  - Widget: "carregando · cheia em ~40 min" embaixo da bateria, só enquanto carrega (opção "Mostrar tempo de carga").
  - Cartão do periférico: "carregando · cheia em ~40 min" ou "na bateria · ~9 h restantes".

## Etapa 07.1: ajustes rápidos (coletor e painel 0.7.1)

- **Renomear máquina**: lápis ao lado do nome (ou dois cliques no nome); Enter grava, Esc cancela, vazio volta ao
  nome do PC. O apelido é só deste PC. O pareamento já sugere o nome do PC no Windows.
- **Subtítulo da máquina**: nome do PC no Windows (quando o apelido é outro) · **placa-mãe** com o fabricante
  abreviado ("MSI MAG X870 TOMAHAWK WIFI") · Windows. Precisa do coletor 0.7.1 (antes mostrava o modelo do sistema).
- **G733**: % pela curva dos headsets G (HeadsetControl, calibrada contra o G HUB: 3,99 V = 94 %, antes 80 %).
  Headsets são lidos a cada 10 s (mouses e teclados a cada minuto) e "desligado" aparece na hora.
- **Transparência do fundo do widget**: botão direito no widget → Transparência do fundo, ou o controle deslizante
  em Configurações. Só o fundo fica transparente; o texto continua sólido.
- **Wi-Fi 7** (multi-link) não aparece mais duplicado ("Wi-Fi 4", "Wi-Fi 5"): fica uma interface por placa.
- Atalho `Corewise.lnk` na raiz do projeto abre o Corewise instalado (`C:\Program Files\Corewise\Corewise.exe`).

## Etapa 07: bateria de periféricos (coletor e painel 0.7.0)

- O coletor lê a bateria de periféricos **Logitech sem fio** pelos dongles USB, com o protocolo **HID++ 2.0** (o
  mesmo do Logi Options+, G HUB e Solaar), sem driver extra e convivendo com o G HUB aberto:
  - **Headsets G Lightspeed** (ex.: G733): pela tensão da bateria (função 0x1F20), convertida em % pela curva de
    descarga do lítio; mostra "carregando" e "desligado".
  - **Receptor Logi Bolt** (ex.: MX Master 3S) e **Unifying** (ex.: K400 Plus): funções 0x1004 / 0x1000 / 0x1001.
  - Busca de aparelhos ao iniciar e a cada 5 min; bateria a cada minuto. Aparelho em repouso ou desligado não
    responde: fica a última leitura, marcada como antiga ("última leitura às…").
  - **Receptor nano** (ex.: Wireless Mouse M275/M280, C52F) a partir da 0.7.2.
  - Outras marcas usam protocolo fechado e não aparecem. **Kit Dell (Dell Universal Receiver 413C:301D)**:
    a coleção do fabricante não aceita comandos e só um relatório (#8) responde, zerado; a Dell só entrega a bateria
    ao próprio software depois de comandos não documentados (testado com `perifericos --escutar 20`).
  - Desligável em `config.json` (`PeripheralsEnabled`). Teste no console: `HwMon.Collector.exe perifericos`.
- Painel: cartão por periférico (ícone de headset/mouse/teclado, "Headset · Lightspeed"), valor da bateria e
  estado; amarelo até 20 % e vermelho até 10 % (carregando não alerta). Histórico da bateria como qualquer métrica.
- Widget: bateria dos periféricos entra no padrão; quem já escolheu as métricas adiciona em "Escolher métricas".
- Ícones novos desenhados no estilo da identidade: `kind-headset`, `kind-mouse`, `kind-keyboard`, `metric-battery`.
- Desenvolvimento: `HwMon.exe --perfil <pasta>` usa outra lista de máquinas e outra cópia (testes sem mexer nos
  pareamentos do dia a dia).

## Etapa 06: histórico (coletor e painel 0.6.0)

- **Coletor** grava `%ProgramData%\HwMon\historico.db` (SQLite, WAL) a partir dos snapshots de 1 s, em três
  resoluções com média, mínimo e máximo: **10 s por 48 h**, **1 min por 30 dias**, **1 h por 2 anos**. Valores como
  inteiros × 1000 (cerca de 35 MB para 48 h de 10 s com ~100 métricas). Só leituras válidas: HDD parado (leitura
  antiga) fica sem ponto em vez de linha reta falsa. Limpeza dos vencidos a cada hora. Desligável em `config.json`
  (`HistoryEnabled`). Não grava capacidades nem contadores desde o boot.
- API: `GET /v1/history?component=&metric=&from=&to=&points=` (escolhe a resolução pelo período e reduz a N pontos
  alinhados ao horário local) e `GET /v1/history/stats?from=&to=` (mín/méd/máx de todas as métricas).
- **Painel**:
  - Cada cartão mostra **"24 h · mín · méd · máx"** da métrica principal (atualiza a cada minuto).
  - **Clicar num cartão abre o histórico**: escolha da métrica (uso, clock, temperatura, consumo…, espaço usado de
    cada volume), período **5 min · 1 h · 24 h · 7 dias · 30 dias · 1 ano**, cartões **Atual / Mínimo (com horário) /
    Média / Máximo (com horário) / Tempo medido**, gráfico com média e faixa de mín–máx (buracos onde o PC estava
    desligado) e dica ao passar o mouse, "histórico gravado desde…", **Exportar CSV** (Excel em português).
  - Rede: **tráfego recebido + enviado** hoje, ontem, 7 dias, este mês e este ano.
  - Os detalhes que antes apareciam ao expandir o cartão estão em "Mais detalhes" na página de histórico.
- `hwmon historico <máquina>` (mín/méd/máx 24 h) e `hwmon historico <máquina> <componente> <métrica> --horas N`.

## Etapa 05: resumo do sistema (coletor 0.5.0)

- Aba **Sistema** na página de cada máquina (ao lado de **Painel**), no estilo CPU-Z, em cartões:
  - **Sistema operacional**: edição, versão, build, instalação, ligado desde; segurança (UEFI, Secure Boot, TPM,
    HVCI, hipervisor).
  - **Placa-mãe e BIOS**: fabricante, modelo, revisão; BIOS (versão, data, SMBIOS).
  - **Processador**: família/modelo/stepping, soquete, núcleos (P + E na Intel), threads, clock base, cache L1/L2/L3,
    microcódigo, instruções (AVX2, AVX-512…).
  - **Memória**: total, slots, máximo suportado e, por pente, fabricante (deduzido do part number quando o SMBIOS diz
    "Unknown"), part number, velocidade nominal e configurada, tensão, ranks. Timings não (o Windows bloqueia o SPD).
  - **Placa de vídeo**: chip, fabricante da placa (pelo subsistema PCI), VRAM real, VBIOS, driver (formato NVIDIA),
    link PCIe máximo e atual.
  - **Armazenamento**: tipo e barramento, firmware, série, rotação, setor, GPT/MBR, link PCIe do NVMe.
  - **Rede**, **Monitores** (EDID: modelo, resolução nativa, tamanho, conexão, ano), **Áudio**, **Energia** (plano;
    bateria e saúde em notebooks).
- "Copiar como texto" (para fórum/suporte) e "Ler de novo". Cada valor pode ser selecionado e copiado.
- Coletor: `GET /v1/system` (lido ao iniciar o serviço e quando o painel pede, no máximo a cada 15 s; não lê SMART,
  então não acorda HDDs). No console: `HwMon.Collector.exe sistema`.
- **Atualizar um coletor já instalado**: dois cliques em `publish\coletor\atualizar.cmd` (pede administrador). Para o
  serviço, troca o executável e reinicia; papel, certificado e pareamentos continuam. Funciona também a partir de
  pasta de rede ou unidade mapeada (copia para `%TEMP%\HwMon-atualizar` antes de elevar, porque a janela de
  administrador não enxerga unidades mapeadas).

## Etapa 04: uso diário (painel 0.4.0)

- **Identidade Hardware Watch** aplicada: ícone do programa, 62 ícones de uma cor, cores de `design/tokens.json`,
  fonte Inter (quando instalada; senão Segoe UI Variable). Página da máquina em **grade única**: CPU e GPU principal
  ocupam duas colunas, e qualquer cartão em atenção/crítico fica largo e sobe para o topo.
  Ícones novos ou alterados em `icons/`: rodar `node design/gerar-icones.mjs` (gera `Controls/IconData.g.cs`).
- **Ícone na bandeja**: clique abre o painel; botão direito tem mostrar widget, sempre no topo, máquina do widget,
  Configurações e Sair. A dica do ícone mostra CPU e RAM de cada máquina.
- **Uma cópia só**: abrir o programa de novo traz o painel para a frente.
- **Fechar não encerra** (padrão): o programa fica na bandeja; sair pelo menu do ícone. Desligável nas Configurações.
- **Iniciar com o Windows**: ligado na primeira execução da cópia publicada (`HKCU\...\Run\HardwareWatch` =
  `"...\HwMon.exe" --inicio`). Ao iniciar abre só o widget (ou painel + widget, ou nada). Se o programa mudar de
  pasta, o caminho é corrigido na próxima execução. Cópias em `bin\` (desenvolvimento) não ligam sozinhas.
- **Configurações** (rodapé da barra lateral): início, bandeja, widget, tema (seguir o Windows, escuro ou claro).
- Argumentos: `--inicio`, `--widget`, `--com-widget`, `--abrir <máquina>`, `--configuracoes`.

## Etapa 03: painel e widget

**Painel** (`publish\painel\HwMon.exe`)
- Barra lateral: Visão geral + uma entrada por máquina com status (verde conectado, amarelo desconectado).
- Visão geral: um cartão por máquina. Workstation destaca CPU, GPU, memória e rede; NAS (papel contém "NAS")
  destaca espaço livre, saúde/temperatura dos discos, rede e CPU.
- Página da máquina: cartões estilo TrueNAS agrupados (Processador, Gráficos, Memória, Armazenamento, Rede,
  Ventoinhas), com valor principal, gráfico dos últimos 2 min, detalhes e volumes com barra de ocupação. Clique no
  cartão para ver mais (clock do núcleo mais rápido, hot spot, horas ligado, totais desde o boot).
- Cores: amarelo/vermelho para temperatura alta, saúde ≠ Saudável e volume acima de 80/90%.
- Leitura antiga (HDD parado, máquina desconectada) aparece esmaecida; o detalhe da fonte e horário fica no tooltip.
- **Personalizar**: marca/desmarca cartões e volumes (ex.: esconder um disco). Ocultos por padrão: redes virtuais
  (Hyper-V/WSL/VPN) e volumes sem letra. Preferência por máquina.
- **Adicionar máquina**: procura o coletor pelo nome/IP, mostra o certificado e pede o código de 6 dígitos.

**Widget**
- Janela compacta, arrastável, posição lembrada. Fixar sempre no topo (alfinete), trocar de máquina (setas),
  engrenagem para escolher as métricas daquela máquina (independente do painel), botão direito para mais opções.
- Fechar o painel com o widget aberto mantém só o widget. `HwMon.exe --widget` abre só o widget.

**Conexão**: o cliente memoriza os IPs que funcionaram e tenta nome e IPs ao mesmo tempo. Com a VPN Surfshark ativa
o DNS da VPN não resolve "NAS"; a conexão segue pelo IP. Preferências em `%AppData%\HwMon\app.json`; erros
inesperados em `%AppData%\HwMon\erro.log`.

## Etapa 02: conectar as máquinas

**Como funciona**
- O coletor roda como serviço `HwMonCollector` (LocalSystem, início automático atrasado), inclusive sem ninguém logado.
- Intervalos: contadores do Windows 1 s; LHM 3 s (sem o módulo de discos); espaço dos volumes 60 s; saúde 10 min;
  SMART 10 min, e em HDD **só se o disco teve leitura/escrita nos últimos 2 min**. Disco parado nunca é consultado;
  o painel mostra a última temperatura como "leitura antiga". Desgaste de SSD/NVMe vem do LHM a cada 10 min, sem tocar nos HDDs.
- API em `https://<pc>:47821/v1/…`: `info` e `pair` públicos; `machine`, `snapshot` e `stream` (Server-Sent Events, 1/s)
  exigem token. Ids estáveis: disco = número de série, volume = GUID, rede = GUID do adaptador.
- Segurança: certificado autoassinado ECDSA criado na instalação; o cliente fixa a impressão digital no pareamento.
  Código de 6 dígitos, válido 10 min, uso único, 5 tentativas, 1 tentativa/s. Tokens guardados como hash no coletor e
  protegidos pelo Windows (DPAPI) no cliente. `revogar` corta o acesso em até 10 s, inclusive com o stream aberto.
- Rede: GB do dia por adaptador, gravados em `%ProgramData%\HwMon\rede-diaria.json` (sobrevivem a reinício do serviço;
  contador que volta não gera pico).
- Dados do coletor em `%ProgramData%\HwMon` (acesso só SYSTEM/Administradores): `config.json`, `certificado.pfx`,
  `clientes.json`. No `config.json` dá para mudar a porta, desligar o LHM ou listar discos em `smartDisabledDisks`.

**Instalar** (em cada PC que será monitorado; pacote em `publish\coletor`)
1. Copie a pasta `publish\coletor` para o PC.
2. `powershell -ExecutionPolicy Bypass -File .\instalar.ps1 -Papel NAS` (ou `-Papel Workstation`). O script pede
   administrador, copia para `C:\Program Files\HwMon\Collector`, instala o serviço, libera a porta no firewall
   (redes privadas) e mostra um código de pareamento.
3. No PC que vai ver os dados (pacote em `publish\cliente`): `.\hwmon.exe parear NAS` e digite o código.
4. `.\hwmon.exe ver NAS`.

Para monitorar a própria workstation, instale o coletor nela também e pareie com `.\hwmon.exe parear localhost --nome Workstation`.

**Aceite da etapa 02** (do roteiro): receber dados do NAS sem sessão aberta nele (sair da conta no NAS), ver
"desconectado desde…" ao reiniciar o NAS e a volta automática quando ele religar.

Testado na workstation (console, só localhost, sem administrador): pareamento com código errado/certo, stream de
35 s sem falhas, queda e volta do coletor com reconexão automática (1, 2, 5 s), revogação com stream aberto.
Testado na workstation com o serviço instalado (07/11/2026): instalação pelo `instalar.ps1`, pareamento com
`localhost`, dados ao vivo e volta automática após `Restart-Service HwMonCollector`.
**Aceite da etapa 02 concluído (07/11/2026, NAS):** pareamento e dados ao vivo pela rede; coletor rodando como
`AUTORIDADE NT\SISTEMA` na sessão 0 (independe de usuário logado); ao reiniciar o NAS o cliente mostrou
"desconectado desde 18:33:55" mantendo os últimos valores, e o coletor voltou a responder às 18:36:25 (início
automático atrasado, ~2 min após o boot).

Achado na instalação do NAS: o nome "NAS" resolve para o IP da LAN e para os IPs das redes virtuais do Hyper-V/WSL
(172.22.x, 172.29.x), inalcançáveis de fora. O cliente agora tenta todos os IPs em paralelo, com prioridade para os
da mesma sub-rede. `hwmon testar <nome>` verifica a conexão sem precisar de código.

Fontes de dados usadas pelos coletores:

| Fonte | Para quê | Precisa de |
|---|---|---|
| `perf` contadores do Windows | uso/clock da CPU, uso/VRAM da GPU, tempo ativo e MB/s dos discos | nada |
| `win32` API do Windows | memória | nada |
| `wmi` Storage Management | discos, volumes, pools, saúde, SMART (temperatura, desgaste, horas) | administrador para SMART |
| `net` estatísticas de interface | taxas e totais por adaptador | nada |
| `lhm` LibreHardwareMonitor 0.9.6 | temperaturas, clocks reais, consumo, ventoinhas, SMART, SPD da RAM | administrador + driver **PawnIO** |

## Primeiro ticket: como rodar

### Pré-requisitos em cada PC
1. Instalar o driver **PawnIO** (https://pawnio.eu). O LHM 0.9.x não usa mais o WinRing0, que o Windows 11 bloqueia com a Integridade de Memória ligada.
2. Copiar a pasta `publish\inventario` para o PC (é um `.exe` único, não precisa instalar o .NET).

### Capturas (em cada PC)
Execute `coletar.ps1`. O script pede elevação sozinho.

```powershell
.\coletar.ps1 -Cenario repouso -Segundos 60
.\coletar.ps1 -Cenario carga -Segundos 60 -CargaCpu   # na workstation, rode também um render para carregar a GPU
.\coletar.ps1 -Cenario copia -Segundos 120            # inicie antes a cópia de um arquivo grande para o NAS, nos dois PCs ao mesmo tempo
.\coletar.ps1 -Cenario hdd-parado -Segundos 30        # no NAS, com os HDDs parados há alguns minutos (testa se a coleta os acorda)
```

Cada captura gera `inventario\<PC>_<cenário>_<data>.json` (dados completos) e `.md` (matriz legível).

### Matriz consolidada
Junte as pastas `inventario` dos dois PCs e rode:

```powershell
.\HwMon.Inventory.exe matriz .\inventario --saida .\inventario\matriz.md
```

### Compilar a partir do código
```powershell
dotnet build HardwareMonitor.sln
dotnet publish src/HwMon.Inventory -c Release -o publish/inventario
```

## O que o relatório registra
- **Estado de cada leitura**: ✅ lido, ⚠️ erro, ❌ indisponível. Um zero válido (ventoinha parada) é diferente de um sensor sem acesso. O LHM devolve `0` quando falta driver ou permissão; em temperatura, consumo e clock, esse zero vira "indisponível".
- **Identidade estável**: o MachineGuid identifica o PC; o número de série (ou UniqueId) identifica cada disco.
- **Volumes → discos → pools** (Storage Spaces), com discos virtuais.
- **Repouso dos HDDs**: estado de energia antes de qualquer consulta, depois do SMART via WMI e depois de abrir o LHM. Mostra qual fonte acorda os discos.
- **Par LHM ↔ disco do Windows**: feito por número de série quando o LHM o expõe. Senão, por nome do modelo, e o relatório avisa quando há discos idênticos.
- **Custo da coleta**: tempo de inicialização, tempo por amostra (por fonte), CPU e memória do processo.

## Status do primeiro ticket: concluído (falta só o teste de repouso dos HDDs)

Matriz final: `publish\inventario\inventario\matriz.md`. Nas capturas `validacao` (versão corrigida) todas as
métricas esperadas têm leitura nos dois PCs, exceto a temperatura da RAM do NAS (limitação do hardware: DDR4 sem
sensor). As colunas das capturas anteriores mostram os erros de mapeamento já corrigidos.

**Fontes escolhidas para o serviço (etapa 02):**

| Métrica | Fonte | Intervalo sugerido |
|---|---|---|
| Uso/clock da CPU, uso/VRAM da GPU, atividade e MB/s dos discos | contadores do Windows | 1 s |
| RAM | API do Windows | 1 s |
| Rede | estatísticas da interface | 1 s |
| Temperatura/consumo da CPU, clock/temperatura da GPU, temperatura da RAM, ventoinhas | LHM (sem o módulo de discos) | 2–5 s |
| Temperatura, saúde, desgaste e horas dos discos | WMI (SMART); LHM só para desgaste de NVMe | 5–10 min, e só se o disco estiver ativo |
| Espaço dos volumes | WMI | 30–60 s |

Medido no piloto: o módulo de discos do LHM custa ~190 ms por leitura nos dois PCs e consulta o SMART a cada
atualização, o que impediria os HDDs de dormir. No NAS, a CPU Intel no LHM custa ~157 ms; na workstation, a NVIDIA ~77 ms.

**Teste de repouso pendente:** o NAS desliga os discos após 20 min ociosos na tomada (`DISKIDLE` = 0x4B0 = 1200 s).
Na captura `hdd-parado` das 16:52, depois de 30 min de espera com o Syncthing pausado, os 3 HDDs estavam girando.
Na workstation, o mesmo método do Windows detectou o disco USB em repouso, então a leitura provavelmente estava
certa e algo manteve os discos do NAS acordados.

Próximo teste: `coletar.ps1 -Energia -Minutos 60` no NAS. Esse modo só observa: usa o comando ATA CHECK POWER MODE
(não acorda o disco) e os contadores de leitura/escrita, sem SMART nem LHM. Ele registra a cada minuto o estado
de cada disco e, se um disco acordar, se houve acesso a arquivos naquele minuto. Grava `<PC>_energia_<data>.csv` e `.md`.

## Resultado do piloto (07/11/2026, administrador + PawnIO nos dois PCs)

Conferido contra o LibreHardwareMonitor e o HWMonitor (`LHM_*.txt`, `HWMonitor_*.txt` na raiz).

| | Workstation (Ryzen 9 9950X, RTX 5070) | NAS (i5-14400, Intel UHD 730) |
|---|---|---|
| CPU uso / clock / temp / consumo | ✅ todos | ✅ todos |
| RAM uso / temperatura | ✅ / ✅ DDR5 (DIMM #1 e #3) | ✅ / **não existe** — DDR4 sem sensor (nem o HWMonitor lê) |
| GPU uso / clock / temp / VRAM | ✅ nas duas | ✅ uso e clock; temperatura própria **não existe** na integrada (usar a do pacote da CPU) |
| Discos: atividade, MB/s, temp, saúde | ✅ 4 discos (inclui o USB) | ✅ 4 discos |
| Desgaste | ✅ NVMe (8%) | ✅ SSD SATA; não se aplica a HDD |
| Rede | ✅ | ✅ |

**Cenário da cópia validado**: workstation enviando ~859 Mbit/s (pico 991) lendo do NVMe a ~103 MB/s; NAS
recebendo ~854 Mbit/s e gravando no disco 3 (F: MEDIA) a ~103 MB/s (pico 190). Os dois lados batem.

**HDDs em repouso: não validado.** Na captura `hdd-parado` os 4 discos do NAS estavam ligados. Falta conferir se o
Windows do NAS desliga os discos (Opções de energia → "Desligar disco rígido após").

**Custo do LHM** é o ponto de atenção: ~300 ms por amostra na workstation e ~370 ms no NAS (inicialização 5–6 s).
Só a leitura da NVIDIA leva ~100 ms. Conclusão para o serviço: contadores nativos a cada 1 s; LHM em thread própria
a cada 2–5 s; SMART de HDD a cada 5–10 min e só com o disco ativo, para não impedir o repouso.

Correções feitas após o piloto (exigem nova captura para aparecer nos relatórios):
- Clock da CPU Intel híbrida (núcleos `P-Core #N`/`E-Core #N`).
- Temperatura da RAM: só o sensor `DIMM #N` (antes entravam os limites fixos 0/55/85 °C e a resolução 0,25).
- GPU listada duas vezes pelo DXGI no NAS (sessão remota): passa a ser uma só.
- Uso da GPU Intel integrada pelo LHM (`D3D 3D`).
- Disco USB "Seagate Expansion Desk" ↔ "ST4000DM000" no LHM: pareado pela capacidade e marcado como HDD.
- NVMe: temperatura "Composite" (não os limites Warning/Critical) e horas ligado pelo LHM.
- Temperaturas fora de 1–125 °C viram erro; tempo do LHM por tipo de hardware no relatório.

## Primeira captura na workstation (07/11/2026, sem administrador e sem PawnIO)
- Funciona sem nada extra: uso e clock da CPU, RAM, uso e VRAM das duas GPUs, tempo ativo e MB/s dos 4 discos, saúde dos discos, espaço dos volumes, rede. Temperatura, clock e consumo da RTX 5070 e da GPU integrada vêm do LHM mesmo sem administrador.
- Faltam: temperatura e consumo da CPU, temperatura da RAM, temperatura e desgaste dos discos. **Repetir como administrador e com o PawnIO** para fechar a matriz.
- A VPN (SurfsharkWireGuard) duplica o tráfego da Ethernet: não somar adaptadores. Na separação LAN/internet ela entra como "Indeterminado".
- Custo: ~120 ms por amostra, ~94 ms dos quais no LHM; CPU do processo ~0,2 %; ~115 MB de memória. No serviço, o LHM deve ficar em um intervalo próprio (2–5 s), separado dos contadores nativos (1 s).
