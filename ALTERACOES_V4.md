# Alterações principais da V4.2 Cyber Consent

- Removida autorização automática no Agent e no servidor.
- Removido estado inicial com acesso/controle já ativos.
- Criada janela visível de consentimento no Agent.
- Visualização, controle, eventos de teclado, bloqueio local e terminal agora possuem autorizações separadas.
- Terminal exige aprovação individual para cada comando e exibe o comando completo ao usuário remoto.
- Bloqueio de input exige aprovação específica, possui botão local de desbloqueio e timeout de 3 minutos.
- Eventos de teclado só são enviados enquanto a permissão correspondente estiver ativa.
- Cada operador do painel recebe sua própria autorização de visualização.
- Controle remoto é exclusivo para um operador por vez.
- Fechar/desconectar o painel remove os direitos daquele operador.
- Reconexão do Agent não restaura permissões antigas.
- Painel atualizado para exibir todos os estados de autorização.
- Movimento do mouse remoto foi ajustado para funcionar sem exigir botão pressionado.
- Scroll remoto foi adicionado ao canvas.
- Métricas de FPS/bitrate/frames/drops são calculadas no próprio painel.
- Logs do servidor deixaram de imprimir o conteúdo integral das mensagens recebidas.
- Binários antigos foram removidos do pacote para evitar uso acidental da versão anterior.


## Correção de teclado
- O painel agora exibe uma única linha por pressionamento físico.
- Eventos KEYUP continuam sendo processados internamente, mas não aparecem como uma segunda linha no histórico.
- Auto-repeat do Windows é filtrado enquanto a tecla permanece pressionada.


## V4.2 — Rework Cyber + Modo de manutenção
- Front redesenhado em estilo cyberpunk/HUD sem alterar os IDs e o fluxo principal do painel.
- Bloqueio comum de teclado/mouse passa a ter limite automático de 3 minutos.
- Novo botão **Solicitar tela protegida — 3 min**.
- Antes de ativar, o Agent informa claramente que a tela ficará preta e que teclado/mouse serão bloqueados.
- A cortina preta mostra **MANUTENÇÃO AUTORIZADA** e contador local.
- Enquanto a cortina está ativa, captura de tela, controle remoto e terminal ficam pausados; não existe operação remota escondida atrás da cortina.
- Ao finalizar, o input local é desbloqueado, a cortina fecha e o stream pode ser retomado se ainda houver visualizador autorizado.
- Encerrar controle, encerrar acesso, desconectar ou revogar localmente também encerra o modo de manutenção.
