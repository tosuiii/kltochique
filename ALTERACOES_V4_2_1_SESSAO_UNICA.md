# V4.2.1 — Consentimento de sessão única

Esta versão simplifica o fluxo principal de testes.

## Um único consentimento inicial cobre
- visualização da tela;
- controle remoto de mouse/teclado;
- bloqueio temporário do input local por até 3 minutos quando acionado;
- modo de manutenção/tela protegida por até 3 minutos quando acionado.

O Agent mostra uma única caixa de autorização explicando esses itens. Ao aceitar, a sessão fica autorizada até ser encerrada, o Agent desconectar ou o usuário clicar em ENCERRAR TODO COMPARTILHAMENTO.

## Mantidos com autorização própria
Por segurança, duas capacidades continuam fora da autorização geral:
- compartilhamento global de eventos de teclado;
- execução de comandos de terminal, que continua pedindo aprovação para cada comando.

## Segurança
- nenhuma autorização sobrevive à desconexão/reinício;
- o usuário local pode revogar tudo a qualquer momento;
- bloqueio e tela protegida continuam com timeout máximo de 3 minutos;
- o modo de manutenção continua pausando stream, controle e comandos enquanto a cortina está ativa.
