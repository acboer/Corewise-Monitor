# Auditoria técnica do Corewise

Documento de execução para Claude • 10 de novembro de 2026

## Contexto e alcance

Projeto analisado em `E:\Hardware Monitor`. Aplicativo Windows de monitoramento local e remoto, inventário, histórico, overlay e benchmarks. A solução utiliza .NET 10, WPF, um serviço com API HTTPS, SQLite e LibreHardwareMonitorLib 0.9.6. Foram inventariados 106 arquivos C# fora de bin/obj. A revisão concentrou-se em API e pareamento, execução e persistência de benchmarks, histórico, telemetria e atualização do painel. O conteúdo do código foi lido diretamente, com referências relativas à raiz do projeto neste documento.

Esta é uma auditoria estática orientada a correções. O aplicativo, o instalador e os benchmarks reais não foram executados. Build e testes automatizados da solução não foram executados. O SDK 10.0.404 está instalado. O acesso do Git ao diretório falhou com Permission denied, portanto o commit e as alterações locais não foram confirmados. As conclusões se referem aos arquivos observados nesta data. Antes de editar, confira os trechos atuais, pois as linhas podem mudar. Não há alegação de cobertura integral de drivers, ETW, periféricos, instalador ou dependências.

Foi reproduzido isoladamente o cálculo de jitter com uma sequência sintética. Os outros achados abaixo possuem evidência no fluxo do código e precisam dos testes de regressão indicados. A CI observada em `.github/workflows/ci.yml` executa apenas `dotnet build ... -c Release`. Não foi identificado projeto de testes na solução examinada.

## Ordem de execução

| Ordem | Prioridade | Trabalho |
|---|---|---|
| 1 | P1 | A01, proteção dos arquivos no benchmark de disco |
| 2 | P1 | A02 e A03, término e falhas dos workers de benchmark |
| 3 | P1 | A04 e A05, integridade do histórico e resultados |
| 4 | P1 | A06, estado de saúde desconhecido no painel |
| 5 | P2 | A07 a A12, exatidão das métricas, API e pareamento |
| 6 | P2 | A13 e A14, ciclo de vida e dados diários |
| 7 | Medição | P01 a P04, desempenho e escalabilidade |

P1 indica risco relevante de perda de dados, indisponibilidade ou informação operacional enganosa. P2 indica correção necessária com impacto mais condicionado ao cenário. A prioridade considera o serviço executado como LocalSystem, conforme o README.

## A01 • O benchmark pode sobrescrever e apagar um arquivo preexistente

**P1 — evidência direta.** `src/HwMon.Core/Collection/DiskBenchmark.cs`, linhas 28, 57–61 e 103–105. O teste usa sempre `HardwareWatch-benchmark.tmp` na raiz do volume, abre com `FileMode.Create` e executa `File.Delete(path)` no finally. Um arquivo anterior com esse nome pode ser truncado. Mesmo quando a abertura falha, a limpeza tenta excluir o caminho. O gate do serviço também não cobre outra instância do executável ou a CLI.

**Correção.** Gerar nome imprevisível por execução, abrir com `CreateNew`, guardar a informação de que o arquivo foi efetivamente criado e limitar a limpeza ao arquivo pertencente àquela execução. Considerar exclusão vinculada ao handle, onde compatível com as flags utilizadas. Validar comportamento de reparse points e localização gravável pelo serviço antes de adotar qualquer diretório temporário compartilhado.

**Aceite.** Um arquivo sentinela com o nome antigo mantém exatamente seus bytes. Colisão de nome, falha de abertura, cancelamento e duas instâncias simultâneas preservam arquivos alheios. O arquivo próprio é removido após sucesso e erro.

## A02 • Espera de GPU ignora cancelamento e prazo

**P1 — evidência direta, travamento depende do driver.** `src/HwMon.Core/Collection/Bench/GpuBenchmark.cs`, `Wait`, linhas 85–90, e `Dispatch`. O loop `while (!_context.IsDataAvailable(_done)) Thread.Sleep(0)` não recebe token nem prazo. Cancelamento é verificado fora dessa espera. A espera também pode consumir CPU enquanto o trabalho da GPU está pendente.

Em `StressTest.cs`, o finally espera a thread GPU por dez segundos e ignora o retorno de `Join`. Em `LoadBenchHost.cs`, `RunStressAsync` chama `gpu.Dispose()` quando a tarefa termina. Assim, se a thread continuar viva após o timeout, há risco de descartar recursos ainda em uso e liberar `BenchGate` com trabalho antigo ativo.

**Correção.** Propagar token e prazo monotônico até a espera, tratar falha/remoção do dispositivo e coordenar a propriedade do contexto. Garantir término do worker antes de descartar seus recursos. Caso seja necessária interrupção forçada de driver travado, avaliar executar benchmarks em processo auxiliar controlado pelo serviço.

**Aceite.** Uma implementação simulada que nunca conclui a query termina por timeout. Cancelar produz estado terminal previsível. Recursos só são descartados depois que o worker para. Uma segunda execução não sobrepõe a primeira.

## A03 • Exceções em threads de CPU escapam do tratamento do host

**P1 — evidência direta do tratamento ausente.** `CpuBenchmark.cs`, método `Measure`; `MemoryBenchmark.cs`, `Bandwidth`; `StressTest.cs`, criação dos workers. Há `new Thread(...)` com alocação e execução de workloads sem captura interna de exceções. O try/catch da tarefa no host não captura automaticamente exceções lançadas por essas threads independentes. Falhas de criação de workers também exigem encerrar os workers já iniciados.

**Correção.** Capturar falhas dentro de cada worker, sinalizar cancelamento coletivo, aguardar todos os workers e devolver a falha ao host. Proteger a sequência de inicialização com finally. Descartar os `ManualResetEventSlim`. Validar retornos de alocação nativa antes de acessar ponteiros nos benchmarks de memória e disco.

**Aceite.** Injetar falha na construção de um workload e em um worker após o início. O serviço continua respondendo e o teste termina como falho, com todos os workers encerrados.

## A04 • Falha ao gravar histórico perde o bloco acumulado

**P1 — evidência direta.** `src/HwMon.Collector/History/HistoryRecorder.cs`, `Flush`, linhas 46–57. Cada acumulador é zerado por `a.Reset()` antes de `store.Write`. Se a escrita falha por falta de espaço, lock ou I/O, o laço de `EngineHost.RecordAsync` registra o erro e continua, mas as amostras já foram descartadas.

**Correção.** Confirmar a transação antes de remover o bloco da memória. Preservar o timestamp original e implementar retry idempotente. Evitar misturar blocos de tempos diferentes durante novas tentativas. Definir limite de memória para uma indisponibilidade prolongada e informar descarte explicitamente.

**Aceite.** Falhar propositalmente na primeira escrita e permitir a segunda. O banco final contém exatamente as mesmas amostras, contagem, média, mínimo e máximo da execução sem falha, sem duplicações.

## A05 • Arquivos JSON de resultados e configuração são regravados diretamente

**P1 — evidência direta; corrupção depende de interrupção ou concorrência.** `BenchmarkHost.cs`, `History`, `Save`, `Delete`; `LoadBenchHost.cs`, linhas 206–238; `DataStore.cs`, linhas 58–69; `Pairing.cs`, `ClientRegistry.Save`. As escritas usam `File.WriteAllText` no destino. Uma interrupção pode deixar JSON parcial. Leitores de histórico não usam o mesmo lock das escritas. `History` transforma `JsonException` em lista vazia, e uma gravação seguinte pode substituir o histórico corrompido por um único resultado. CLI e serviço também compartilham estado de clientes, com locks que só funcionam dentro de um processo.

**Correção.** Escrita em temporário exclusivo no mesmo volume seguida de substituição atômica, recuperação por backup e diagnóstico de corrupção. Coordenar leitura-modificação-escrita entre processos onde a CLI participa, ou encaminhar mutações ao serviço. Preservar o último estado íntegro e distinguir ausência de arquivo de falha de leitura.

**Aceite.** Simular interrupção na gravação, leitura simultânea, delete concorrente com save e revogação pela CLI durante atividade do serviço. Resultados preservados e cliente revogado permanece revogado.

## A06 • Discos sem leitura são apresentados como saudáveis

**P1 — evidência direta.** `src/HwMon.App/ViewModels/MachineViewModel.cs`, `UpdateTiles`, aproximadamente linhas 823–828. `bad` conta somente notas não nulas diferentes de “Saudável”. Se todas as notas forem nulas, o painel mostra todos os discos como saudáveis. O cálculo também não exige `ReadingState.Ok`.

**Correção.** Contar saudáveis, alerta/falha, desconhecidos e antigos usando estado e conteúdo da leitura. Exibir a quantidade desconhecida e preservar a indicação de perda de conexão. Preferir um código de saúde no protocolo e traduzir apenas a apresentação.

**Aceite.** Testar todos nulos, uma leitura saudável e uma ausente, falha conhecida, leitura stale e máquina desconectada. Somente leituras válidas de saúde compõem a contagem de saudáveis.

## A07 • O jitter é calculado depois de ordenar as latências

**P2 — cálculo reproduzido isoladamente.** `src/HwMon.Client/NetBenchmark.cs`, `RunAsync`. `rtts.Sort()` precede a média de `Abs(b-a)` dos pares consecutivos. A ordenação remove a sequência temporal e transforma o cálculo em uma medida de espaçamento entre valores ordenados.

Exemplo reproduzido com a mesma fórmula: para `[1, 100, 1, 100]` ms, a média das variações temporais é 99 ms. Após ordenar, o código produz 33 ms.

**Correção.** Calcular a variação sobre a sequência original e ordenar uma cópia para a mediana. Para vinte amostras, calcular a mediana pelos dois elementos centrais. Documentar a definição adotada como variação de RTT da aplicação HTTPS.

**Aceite.** Testar sequência constante, alternada, crescente, número par de elementos e amostras insuficientes. Manter o vetor original intacto.

## A08 • GPU com zero de memória dedicada é descartada

**P2 — evidência direta.** `GpuBenchmark.cs`, linhas 59–70. `bestMemory` começa em zero e o filtro descarta qualquer adaptador com `DedicatedVideoMemory <= bestMemory`. Uma GPU integrada que reporte zero é excluída mesmo que suporte a API. A escolha de maior VRAM também precede a tentativa de criação do dispositivo, sem fallback entre candidatos compatíveis.

**Correção.** Aceitar o primeiro candidato de hardware e testar suporte ao criar o dispositivo. Permitir selecionar GPU e informar a GPU efetivamente testada. Dimensionar buffers de banda conforme orçamento disponível, pois hoje são dois buffers fixos de 512 MiB.

**Aceite.** Simular GPU integrada com zero de VRAM dedicada, múltiplas GPUs e primeiro candidato incompatível. O adaptador compatível é utilizado e identificado corretamente.

## A09 • Pareamento por aceite possui operações compostas sem sincronização

**P2 — evidência direta; concorrência precisa de regressão.** `src/HwMon.Collector/PairRequests.cs`, `Ask`, `Status` e `Answer`. `ConcurrentDictionary` protege operações do dicionário, mas a sequência contar/adicionar e os campos mutáveis de `Entry` ficam sem coordenação. Duas aprovações podem emitir tokens diferentes. Duas consultas podem observar a mesma entrada antes da remoção. `Answer` não checa a validade de dois minutos. `Status` remove a entrada concluída antes de confirmar que o cliente recebeu a resposta, dificultando recuperar um token após falha de rede.

**Correção.** Transição de estado atômica com prazo validado no momento da aprovação. Fazer o limite de pedidos participar da mesma sincronização. Projetar recuperação autenticada/idempotente da resposta ou um acknowledgement explícito, com expiração e proteção do segredo.

**Aceite.** Aprovações paralelas emitem um único token válido. Pedido expirado não é aprovado. Chamadas simultâneas respeitam o limite. Perder a resposta de aprovação tem recuperação definida.

## A10 • Pareamento pode gerar fila ilimitada e valores nulos chegam a dereferences

**P2 — evidência direta.** `src/HwMon.Collector/Program.cs`, linhas 117–150 e 154–158. O endpoint público usa `pairGate.WaitAsync()` sem token de desconexão e uma espera de um segundo por requisição. Não há limite explícito para a fila. `request.ClientName.Length` é avaliado sem tratar null. Em `PairingCodes.Verify`, `code.Trim()` também pressupõe valor não nulo. Declaração C# não anulável não substitui validação do corpo JSON.

**Correção.** Validar campos explicitamente e retornar 400 para payload inválido. Adicionar limite de concorrência/fila e rate limit com 429, ligando a espera a `RequestAborted`. Preservar a limitação de tentativas do código.

**Aceite.** Campos omitidos, null, strings longas e requisições canceladas geram respostas previsíveis. Rajada de pedidos não impede pareamento legítimo indefinidamente nem acumula espera sem limite.

## A11 • Benchmarks de rede escapam do controle de carga compartilhado

**P2 — evidência direta.** `Program.cs`, linhas 230–255, comparadas com `BenchGate` em `LoadBenchHost.cs`. Download e upload aceitam requisições autenticadas paralelas sem participar do controle de benchmarks. O upload remove o limite de tamanho e lê até EOF, sem prazo próprio. Vários clientes podem saturar rede e CPU junto a um teste de disco/CPU/GPU e contaminar resultados.

**Correção.** Criar sessão de benchmark com duração máxima, cota de fluxos e coordenação com o gate. Preservar os quatro fluxos do cliente como parte de uma única execução. Limitar upload no servidor por tempo/volume e tratar cancelamento de toda a sessão.

**Aceite.** Dois painéis concorrentes recebem admissão ou recusa explícita. Um upload que continua transmitindo termina pelo limite. O cancelamento libera todos os fluxos e recursos.

## A12 • Cancelamento da medição de rede pode provocar loop acelerado

**P2 — evidência direta do fluxo.** `NetBenchmark.cs`, `MeasureAsync`. O loop espera `Task.WhenAny(all, Task.Delay(250, cancel))`. Depois que o token cancela, o delay completa imediatamente. Enquanto as transferências ainda encerram, o laço pode produzir amostras e callbacks rapidamente. Se todas as transferências terminarem antes de entrar no laço, `samples[^1]` acessa uma lista vazia.

**Correção.** Interromper o laço ao cancelar, aguardar o encerramento das transferências e capturar uma amostra final explicitamente. Tratar duração e amostragem insuficientes. Usar relógio monotônico também nas janelas de transferência hoje baseadas em `DateTime.UtcNow`.

**Aceite.** Transferências simuladas com conclusão imediata e cancelamento com encerramento atrasado não geram exceção de índice nem explosão de callbacks.

## A13 • Tarefas de gravação e benchmarks têm ciclo de vida desacoplado do serviço

**P2 — evidência direta.** `EngineHost.ExecuteAsync` inicia `RecordAsync` com fire-and-forget e descarta a engine no finally. Os hosts de benchmarks guardam CTS, mas descartam o handle da tarefa e não vinculam seu cancelamento ao encerramento do host. Isso dificulta assegurar gravação concluída, arquivo temporário removido e recursos liberados antes de o processo terminar.

**Correção.** Guardar e aguardar tarefas de longa duração no shutdown, vinculando os tokens ao host. Descartar CTS anteriores depois do término. No histórico, definir persistência de blocos parciais no desligamento normal. O comentário de `HistoryRecorder` reconhece perda de até uma hora da resolução horária em cada parada.

**Aceite.** Parar o serviço durante coleta e durante benchmark encerra cada tarefa com ordem definida. Histórico preserva blocos parciais sem duplicação ao reiniciar. O arquivo de teste é removido.

## A14 • Totais diários incorporam o intervalo anterior à virada do dia

**P2 — evidência direta, erro proporcional ao intervalo sem coleta.** `src/HwMon.Core/Telemetry/NetDailyTotals.cs`, linhas 35–55. Ao mudar a data, os totais zeram, porém `LastRx` e `LastTx` continuam antigos. A diferença completa é atribuída ao novo dia. Após serviço parado desde o dia anterior, na mesma inicialização do Windows, o desvio pode representar horas de tráfego.

**Correção.** Persistir o instante da amostra e definir política explícita para intervalos que cruzam dias. Preferir marcar período sem observação e estabelecer nova base quando a atribuição exata não for possível. Tratar reset de RX e TX independentemente, pois hoje um reset em um sentido descarta o incremento válido do outro.

**Aceite.** Testar meia-noite, serviço parado por horas, reset de um contador, reinício do Windows e ajuste de relógio. Totais devem refletir a política documentada.

## Oportunidades de desempenho com medição obrigatória

### P01 • Compartilhar a serialização do snapshot entre clientes

`Program.cs`, endpoint `/v1/stream`, linhas 373–399, obtém e serializa um snapshot por conexão. O custo cresce com a quantidade de painéis. Avaliar publicar um snapshot imutável serializado por sequência, distribuído aos assinantes, mantendo revogação periódica e tratamento de cliente lento. Medir bytes alocados/s, CPU e latência com 1, 3 e 10 clientes antes e depois. Evitar cache que associe uma sequência ao conteúdo de outra.

### P02 • Conter fila de atualizações da interface

`MachineViewModel.cs`, linhas 243–256, enfileira `dispatcher.InvokeAsync` pelo callback sem aguardar a operação. Se a UI estiver ocupada, snapshots antigos podem acumular. `Stop` cancela a conexão, mas callbacks já enfileirados podem executar após um novo `Start`. Usar geração da conexão e agregação para aplicar o snapshot mais recente. Medir atraso do painel e tamanho da fila com UI bloqueada temporariamente e várias máquinas.

O mesmo arquivo copia filas para arrays em `History` e recompõe históricos a cada `Refresh`. O buffer atual é de 120 pontos, então o ganho isolado pode ser pequeno. Medir antes de introduzir abstrações. Atualizar gráficos visíveis e reutilizar dados quando a sequência não mudou são candidatos.

### P03 • Reduzir varredura da limpeza do histórico

`HistoryStore.cs` cria chave primária `(series,t)` e limpa por `WHERE t < $limit`, a cada hora em `EngineHost`. Avaliar `EXPLAIN QUERY PLAN` com banco representativo e comparar índice adicional por `t`, custo de escrita e tamanho do banco. Evitar prescrever índice sem medir a troca entre escrita contínua e limpeza. Registrar duração p50/p95 das consultas e limpezas.

### P04 • Reduzir espera ativa na GPU e preservar comparabilidade

O polling de `GpuBenchmark.Wait` com `Thread.Sleep(0)` merece medição de CPU. Implementar espera com backoff curto e limites, então verificar o efeito na pontuação. Registrar versão do algoritmo, parâmetros, GPU selecionada e ambiente nos resultados. `CpuBenchmark.Reference` contém calibração fixa por workload. Mudanças de algoritmo precisam de versão para evitar comparação de escalas incompatíveis.

## Melhorias de exatidão e produto

- Os históricos locais do painel armazenam somente números válidos e descartam timestamps (`MachineViewModel.Apply`). Um intervalo sem sensor pode aparecer como continuidade visual. Guardar tempo e lacunas, especialmente ao somar históricos de consumo de componentes com disponibilidade diferente.
- `StressTest.Analyze` classifica uma queda de clock de 5% como throttling por temperatura ou consumo. A métrica sozinha sustenta “queda de clock observada”. Confirmar a causa com indicadores de limitação e contexto da carga antes de apresentar um diagnóstico definitivo.
- `LoadBenchHost.SampleAsync` escolhe a GPU pelo maior valor de VRAM observado, enquanto o benchmark usa seleção DXGI. Compartilhar a identidade da GPU executada para evitar atribuir temperatura e potência de outra placa ao resultado.
- O pareamento automático por loopback em `Program.cs`, linhas 128–137, concede token a processos locais, inclusive de usuários sem privilégios. Como o token também permite iniciar testes e apagar históricos, documentar e decidir explicitamente a política de autorização. Avaliar escopos de leitura e operação, aprovação local ou IPC com identidade Windows. Não tratar a localização loopback como identidade do usuário.
- O README afirma ausência de risco de anti-cheat e ausência de acesso à internet. A ausência de injeção é um fato de implementação a validar, enquanto garantias absolutas precisam de evidência adicional. Conferir chamadas externas e comportamento com jogos antes de publicar essas afirmações.

## Plano de validação para execução pelo Claude

1. Registrar commit, alterações locais, SDK e versões dos pacotes. Ler instruções do repositório e reproduzir o build Release. Preservar alterações em andamento do autor.
2. Implementar A01 primeiro, com teste de preservação do arquivo sentinela em diretório temporário. Usar operações de arquivo substituíveis para cobrir falhas sem executar carga pesada em disco real.
3. Adicionar testes de regressão focados em A02–A14. Injetar relógio, armazenamento e conclusão de GPU somente nos pontos necessários para reproduzir as falhas. Aproveitar estruturas existentes.
4. Cobrir endpoints com testes de integração de autenticação, payload inválido, limites de concorrência, cancelamento e revogação durante stream.
5. Usar dados simulados para zero, ausência, stale e desconexão. Conferir painel e widget, inclusive com terceiro PC cadastrado e conexão reaberta rapidamente.
6. Executar teste funcional de serviço e painel em máquinas piloto, com cancelamento de cada benchmark e recuperação de conexão. Registrar hardware, versões e limitações observadas.
7. Medir desempenho em repouso, com widget, com painel e com vários clientes. Publicar comparação antes/depois com duração e condições do teste. Ganhos percentuais só podem ser declarados após essas medições.
8. Acrescentar a execução dos testes à CI. Revisar documentação e compatibilidade de dados já persistidos quando alterar formatos, parâmetros ou versões de benchmark.

## Instrução pronta para o Claude

Audite os achados deste documento contra o código atual de `E:\Hardware Monitor` e implemente as correções na ordem de prioridade indicada. Para cada achado, confirme o fluxo e seus chamadores, faça a menor alteração que resolva o problema e acrescente o teste de regressão correspondente. Se o código tiver mudado, registre o trecho que já resolveu o problema ou a razão concreta para ajustar a recomendação. Preserve formatos e dados antigos por migração explícita quando necessário. Execute build Release e os testes disponíveis. Entregue relação de arquivos alterados, resultados das verificações, limitações pendentes e medições antes/depois para otimizações. Use dados temporários nos testes de falha e confirme que nenhum teste de disco pode atingir arquivos preexistentes.
