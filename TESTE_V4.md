# Checklist de teste — V4 Consent

Faça o teste com dois computadores que você controla.

## 1. Servidor

- Defina `ADMIN_TOKEN` e `AGENT_KEY` no Railway/Render.
- Faça o deploy de `RealtimeServer`.
- Abra `/health` e confirme `status: ok`.

## 2. Agent

- Execute `Agent/BUILD_AGENT.bat`.
- Informe a URL `wss://...` e a mesma `AGENT_KEY` do servidor.
- Abra o EXE gerado.
- Confirme que a janela `KL TOCHIQUE — Sessão de Suporte` fica visível.
- Antes de qualquer solicitação, todos os estados devem aparecer bloqueados/desativados.

## 3. Painel

- Abra `Panel/index.html` no host do painel.
- Informe URL WSS, `ADMIN_TOKEN` e nome do operador.
- Confirme que o Agent aparece na lista.

## 4. Visualização

- Clique em `Solicitar acesso`.
- O Agent deve exibir `Solicitação de visualização`.
- Teste `Não`: a tela não deve ser transmitida.
- Solicite novamente e teste `Sim`.
- Clique em `Ver ao vivo`.
- A imagem deve começar a aparecer.

## 5. Controle remoto

- Clique em `Solicitar controle`.
- O Agent deve exibir uma confirmação separada.
- Teste negar e aceitar.
- Depois de aceitar, mova o mouse dentro do canvas e teste clique, teclado, scroll e teclas especiais.
- No Agent, clique em `Revogar controle` e confirme que o painel perde a autorização.

## 6. Eventos de teclado

- Clique em `Solicitar eventos de teclado`.
- O Agent deve avisar explicitamente que o conteúdo digitado pode ser revelado.
- Somente depois de aceitar os eventos devem aparecer no painel.
- Teste `Parar eventos de teclado` no painel e `Parar teclado` no Agent.

## 7. Bloqueio temporário

- Com controle remoto autorizado, clique em `Solicitar bloqueio de teclado/mouse`.
- O Agent deve pedir confirmação própria.
- Se aceito, teste o botão local `Forçar desbloqueio`.
- Teste novamente sem desbloquear e aguarde 3 minutos; deve desbloquear automaticamente.

## 8. Terminal

- Com visualização e controle autorizados, envie um comando de teste simples, por exemplo `whoami`.
- O Agent deve mostrar o comando completo antes de executar.
- Teste negar: nada deve ser executado.
- Teste aceitar: o resultado deve voltar ao painel.
- Repita com outro comando e confirme que aparece uma nova aprovação — a autorização não é reaproveitada automaticamente.

## 9. Revogação total

- Com várias permissões ativas, clique em `ENCERRAR TODO COMPARTILHAMENTO` no Agent.
- Tela, controle e eventos de teclado devem parar imediatamente.
- O painel deve voltar a mostrar as permissões como bloqueadas.

## 10. Queda de conexão

- Com permissões ativas, feche o painel ou interrompa a rede.
- O Agent deve remover permissões relacionadas ao operador.
- Reinicie o Agent e confirme que nenhuma permissão anterior reaparece automaticamente.


## Teste do modo Tela Protegida
1. Autorize visualização e controle normalmente.
2. No painel, clique em **Solicitar tela protegida — 3 min**.
3. No PC Agent, confira se o aviso diz explicitamente que a tela ficará preta, o input local será bloqueado e o stream/controle remoto serão pausados.
4. Clique em **Não**: nada deve mudar.
5. Repita e clique em **Sim**.
6. O PC Agent deve mostrar a tela preta **MANUTENÇÃO AUTORIZADA** com contador.
7. O painel deve mostrar `MODO MANUTENÇÃO` sobre o viewer e não deve aceitar mouse, teclado ou comandos durante esse período.
8. Use **Encerrar tela protegida** ou aguarde 3 minutos.
9. A tela local deve voltar, teclado/mouse devem ser liberados e o stream deve retomar quando houver visualizador autorizado.
10. Repita encerrando o controle/acesso durante o modo; a cortina também deve ser removida.
