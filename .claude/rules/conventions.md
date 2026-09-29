# Convenções & Invariantes — LayoutParserDecrypt

Regras que **todo agente** deve respeitar ao editar este repo. Violar qualquer uma quebra a integração com os outros 3 repositórios.

## 1. Contrato de CLI é congelado
- Ordem dos argumentos: `<inputFile> <outputFile> [correlationId] [logDir]`.
- Exit codes: `0` = sucesso, `1` = qualquer falha.
- Consumido por `LayoutParserApi/Services/Database/DecryptionService.cs`. Mudar a ordem/semântica quebra a API em produção.

## 2. O strip de 3 caracteres mora no `Program.cs`
- `encryptedContent.Substring(3)` acontece **antes** de chamar `CryptographySysMiddle.Decrypt`.
- A lib **não** faz esse strip. Não mover sem alinhar com `lpd-integrator` + API.

## 3. `LayoutParserLib/` é a fonte da verdade da cripto/logger
- `LayoutParserLib/CryptographySysMiddle.cs` e `LayoutParserLib/RollingFileLogger.cs` vivem **só aqui**; o repo `layoutparser-lib` foi arquivado. Não há sincronização com outro repo.
- Chave/IV hardcoded: não altere nem rotacione sem decisão explícita do dono do projeto.
- O `.csproj` inclui essas cópias como fontes locais para manter o build de CI autocontido — não troque por `<ProjectReference>` sem rever o `build.yml`.

## 4. Segredos
- Chave/IV estão hardcoded em `CryptographySysMiddle.cs`. Não adicione novos segredos no código. Se for externalizar, documente no README e alinhe com a API.

## 5. Logging nunca lança
- Os dois `RollingFileLogger` engolem exceções (`catch {}`). Mantenha esse comportamento: log jamais deve derrubar a descriptografia.

## 6. Build & validação
- Build: `msbuild` Release (`AnyCPU`). Não há `dotnet test`.
- Validação de mudança comportamental = roundtrip real (`/decrypt-roundtrip`). Sempre rode antes de concluir.

## 7. Git
- `git push` e PRs só quando o usuário pedir explicitamente. Commits locais idem.
