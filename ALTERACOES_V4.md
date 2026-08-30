# Alterações principais da V4

- Removida autorização automática no Agent e no servidor.
- Removido estado inicial com acesso/controle já ativos.
- Criada janela visível de consentimento no Agent.
- Visualização, controle, eventos de teclado, bloqueio local e terminal agora possuem autorizações separadas.
- Terminal exige aprovação individual para cada comando e exibe o comando completo ao usuário remoto.
- Bloqueio de input exige aprovação específica, possui botão local de desbloqueio e timeout de 30 segundos.
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
