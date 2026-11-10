# Como instalar o Corewise (versão de teste)

O Corewise mostra temperaturas, uso, ventoinhas, discos, rede, bateria de headset/mouse, FPS em jogos e benchmarks
de um ou vários PCs da sua rede, num painel e num widget. Funciona em português e em inglês (segue o idioma do
Windows; dá para trocar em Configurações).

## Instalar

1. Baixe `CorewiseSetup-0.13.1.exe` e dê dois cliques.
2. O Windows deve avisar **"O Windows protegeu o computador"** (o instalador ainda não tem assinatura digital).
   Clique em **Mais informações** → **Executar assim mesmo**.
   Se em vez disso aparecer **"O controle inteligente de aplicativos bloqueou um aplicativo"**, não há botão para
   liberar: o Windows está com o *Controle inteligente de aplicativos* ligado, que bloqueia todo programa sem
   assinatura digital. Enquanto o Corewise não for assinado, a única saída é desligá-lo em **Segurança do Windows ›
   Controle de aplicativos e do navegador › Configurações do Controle inteligente de aplicativos › Desativado**.
   Nas versões mais antigas do Windows 11 ele não pode ser religado depois sem reinstalar o Windows.
3. Escolha o tipo:
   - **Completo**: para o seu PC. Instala o painel, o widget e o serviço que lê os sensores.
   - **Só o serviço**: para um PC que você quer acompanhar de outro (servidor, NAS, PC de render). Não abre nada na
     tela.
   - **Só o painel**: para acompanhar outros PCs sem ler os sensores deste.
4. No fim, o Corewise abre e o próprio PC aparece sozinho na lista (em alguns segundos).

Precisa de Windows 10 ou 11 de 64 bits. O serviço roda como sistema porque é o único jeito de ler os sensores da
placa-mãe, da CPU e dos discos; ele não envia nada para a internet, só responde aos painéis pareados na sua rede.

## Ver outro PC no painel

1. No seu painel: **Adicionar máquina**, digite o nome (ou o IP) do outro PC e clique em **Procurar**.
2. Clique em **Pedir acesso**. No outro PC aparece uma janela **"… quer acompanhar este PC"**: alguém lá clica em
   **Permitir**. Pronto.

Se o outro PC tem **só o serviço** (sem o painel aberto), ninguém vê o pedido. Nesse caso use o código: no outro PC,
**Iniciar › Corewise › Código de pareamento** (ou, se ele tiver o painel, **Configurações › Mostrar código de
pareamento**, ou o menu do ícone perto do relógio). No seu painel, abra **Tenho um código**, digite e **Parear com
código**.

Se não encontrar o outro PC: os dois precisam estar na mesma rede, e a rede do Windows precisa estar como
**Privada** (Configurações › Rede e Internet › sua conexão › Tipo de perfil de rede).

## Atalhos e widget

- **Ctrl+Shift+F10** mostra ou esconde o FPS por cima do jogo; **Ctrl+Shift+F9** mostra ou esconde os widgets. Os
  dois podem ser trocados em Configurações (clique na caixa do atalho e aperte a combinação nova).
- O widget pode ser **um só** (as setas trocam de PC) ou **um para cada PC**, e você escolhe quais PCs aparecem.

## Atualizar e desinstalar

- **Atualizar**: rode o instalador novo por cima. Nada se perde.
- **Desinstalar**: Configurações do Windows › Aplicativos › Corewise. O histórico fica em `C:\ProgramData\Corewise`
  (apague a pasta se quiser limpar tudo).

## Problemas

Mande a pasta `%AppData%\Corewise` (tem o `erro.log`) e uma captura da tela.
