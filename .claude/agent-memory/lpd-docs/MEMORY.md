# Lia (Documentação) — Memória do Agente

Decisões editoriais e pontos que confundem leitores. Ancorado em arquivos reais.

## Key Patterns
- Documentação **humana** = [`README.md`](../../../README.md); documentação **de máquina** = [`.claude/CLAUDE.md`](../../CLAUDE.md). Não duplicar; cruzar com links.
- Diagrama do pipeline usa **mermaid** (renderiza no GitHub). Manter `LayoutParserDecrypt` destacado no diagrama.
- Público inclui banca de faculdade → explicar o "porquê" (decisões), não só o "como".

## Pontos que confundem leitores (sempre explicar)
- A existência do `.exe` separado parece redundante até explicar a incompatibilidade net10.0 ↔ cripto legada.
- O strip de 3 caracteres não está na lib — fácil o leitor assumir que `Decrypt` faz tudo.
- A cripto vive só em `LayoutParserLib/` deste repo (fonte da verdade; o repo `layoutparser-lib` foi arquivado).

## Gotchas
- **Validar contra o código antes de escrever.** Documentação reflete o código real; nada de suposição.
- Nenhum dos 4 repos tinha README antes deste trabalho — não há doc legada para reaproveitar.
