# A3 — Streamable HTTP

**Estado 2026-10-08:** A3.1 y A3.2 validadas end-to-end con complemento HTTP separado; A3.3 confirmó concurrencia para llamadas de lectura, pero no mejora sostenida en llamadas remotas `process_run`. Se mantiene STDIO productivo (Release 6), y HTTP experimental separado. El Host HTTP y su túnel se reiniciaron desde IvanSpace después del cutover de Release 6. Ver [[A3.2 - Benchmark ChatGPT HTTP]] y [[A4.1 - Release 6 desplegada]].

## Objetivo

Ampliar el paralelismo de ChatGPT normal usando LoomLCI local, sin consumir cupos de Codex/Work ni API OpenAI.

## A1 y A2: causa y alternativa

- Host STDIO directo: 3 process_run con espera de 1,6 s terminaron en 1,87 s en paralelo vs. 5,54 s secuencial.
- ChatGPT/túnel STDIO compartido: en el ensayo A2, 3 llamadas `process_run` terminaron en 11,23 s y comenzaron escalonadas. **Esto no demuestra que `tunnel-client` 0.0.14 serialice todas las solicitudes:** A3.3 observó lecturas MCP concurrentes tanto por STDIO como por HTTP.
- Mediante un solo python_execute con loom.process.start: tres procesos superpuestos en ~1,95 s de trabajo local. Es el workaround disponible ahora.
- La versión instalada de tunnel-client ofrece --mcp-server-url y cabeceras MCP. ModelContextProtocol.AspNetCore 2.2.0 está disponible.

## A3.1: implementación

Proyecto experimental fuera de solución: experiments/LoomLCI.HttpPoc/ (README y smoke.py). Sin cambios en Host STDIO, Launcher, perfil, runtime instalado o secreto del túnel.

- ASP.NET Core + MCP Stateless, ruta /mcp, health protegido.
- Kestrel sólo en 127.0.0.1, puerto configurable.
- Bearer aleatorio obligatorio (mínimo 32 bytes), hash SHA-256 comparado en tiempo constante, Host estricto, Origin rechazado, sin CORS; arranque sin secreto falla.
- Mismas 25 clases de herramientas y capacidades Core/Windows/PdfWorker, con WorkSessions persistentes en el Host.
- Incluye worker PDF internal para conservar compatibilidad de tools.

## Pruebas y evidencias

- Build Release: OK, 0 warnings, 0 errors. Suite Release de la solución existente: **366/366 PASS**.
- Autenticación: 401 ausente/incorrecta, 403 Host/Origin falso, 200 autorizado, fail-closed sin token.
- Protocolo initialize MCP 2025-11-25 + tools/list: 25.
- Contrato idéntico a Release 5 STDIO: 0 diferencias en nombres, títulos, descripciones, inputSchema, outputSchema o annotations.
- WorkSession, work_plan_update/get y filesystem_read_files entre múltiples solicitudes HTTP: PASS.
- 3 process_run de 1,6 s: secuencial 5,547 s; paralelo 1,867 s, todos código 0.
- Smoke repetible de 1,2 s: 4,345 s vs. 1,494 s; todas las fases PASS.
- WorkSession cerrada y Host de prueba detenido.

## Limitaciones y siguientes pasos

A3.1 demuestra concurrencia local, NO demuestra mejora real desde ChatGPT. Falta probar la autenticación del túnel HTTP y su paralelismo real. El PoC no está diseñado como servicio productivo, no tiene integración con Launcher o rollback ni storage de clave local persistente. No modificar LoomLCI instalado ni sustituir STDIO sin pruebas aisladas de un túnel distinto y plan de retorno. Aunque los 25 contratos coinciden, validar PDF/Python/imágenes por separado antes de adoptar.

Fuente SDK: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/stateless/stateless.md

## A3.2 — Perfil HTTP de túnel aislado

Estado de la fase de preflight (histórico, antes del E2E): preflight local COMPLETADO; E2E remoto en ChatGPT todavía pendiente en ese momento por falta de segundo túnel y clave administrativa. **El E2E se completó después**, como se documenta más abajo.

- tunnel-client 0.0.14 soporta --mcp-server-url y encabezados MCP extra/discovery con secretos referenciados por env: o file:.
- El perfil admin local apunta a una variable de entorno no definida. Crear un nuevo túnel requiere admin key y Tunnels Read+Manage. No se reutilizó el tunnel ID productivo, perfil ni runtime key instalados.
- experiments/LoomLCI.HttpPoc/tunnel_preflight.py inicia el Host HTTP con Bearer y puerto efímeros, crea perfil y estado temporales con ID sintético y clave ficticia, ejecuta tunnel-client doctor y limpia todo. NO llama a la plataforma remota.
- Se corrigió A3.1: los GET exactos /.well-known/oauth-protected-resource y /.well-known/oauth-protected-resource/mcp devuelven 404 sin token para descubrimiento OAuth opcional; todos los endpoints funcionales siguen exigiendo token y la validación Host/Origin permanece.
- doctor local: PASS. Importante: doctor PASS NO demuestra que el ID sintético exista ni que la clave de prueba funcione con OpenAI.
- Smoke tras el cambio: PASS, 25 contratos sin diferencias; tres procesos secuenciales 4.312 s, concurrentes 1.512 s.

Pendiente E2E: crear segundo tunnel ID real asociado al workspace, runtime API key restringida Tunnels Read+Use y plugin de prueba. Mantener credenciales fuera de repositorio/chat; un secreto user-only y encabezados localmente referenciados. Medir process_run desde el segundo plugin y comparar con A2, sin modificar loomlci-installed.

Fuentes:
- https://developers.openai.com/api/docs/guides/secure-mcp-tunnels
- https://github.com/openai/tunnel-client/blob/master/docs/configuration.md

## A3.2 - Túnel HTTP remoto real iniciado (2026-10-08)

Se creó manualmente en OpenAI Platform el túnel LoomLCI HTTP Test, ID tunnel_6ac73f6a8cfc8191b551818597fc5413. El túnel STDIO productivo es diferente y no se modificó.

- Clave de runtime restringida en %LOCALAPPDATA%\LoomLCI\http-test\secrets\runtime-api-key.txt, con ACL de usuario exclusiva; nunca guardar valor en Git ni documentación.
- Bearer HTTP local independiente y aleatorio en %LOCALAPPDATA%\LoomLCI\http-test\secrets\local-bearer-header.txt, ACL exclusiva del usuario. Contiene un encabezado Bearer completo; nunca imprimirlo.
- Perfil a32-http-test en %LOCALAPPDATA%\LoomLCI\http-test\profiles\a32-http-test.yaml. Referencia a la clave remota mediante file: y apunta a http://127.0.0.1:57831/mcp.
- Estado del cliente en %LOCALAPPDATA%\LoomLCI\http-test\state; MCP_EXTRA_HEADERS y MCP_DISCOVERY_EXTRA_HEADERS usan Authorization: file:<ruta_absoluta_al_bearer_local>, no contienen la credencial literal.
- Host experimental lee LOOMLCI_HTTP_POC_TOKEN_FILE desde archivo protegido (nuevo soporte de A3.2) sin modificar el Host STDIO instalado. El modo de token literal por entorno sigue disponible en los smoke tests.
- En la prueba original los procesos se lanzaron con ownership Independent respecto de la WorkSession (Host PID 16012 y tunnel-client PID 10164); esos handles **son históricos y ya no son válidos**. Durante el primer intento de cutover de Release 6 el árbol de procesos de LoomLCI productivo se detuvo y ambos procesos experimentales dejaron de responder. Luego se reiniciaron externamente desde IvanSpace. No hay autoarranque tras reinicio Windows.
- Host /health autenticado: HTTP 200; tunnel-client doctor: PASS. Tunnel-client recibió metadatos remotos, inicializó MCP y arrancó. Endpoint local de salud http://127.0.0.1:61161/healthz = 200 y /readyz = 200. Advertencia no fatal por discovery OAuth 404.
- Se mantuvo LoomLCI Release 5 productivo healthy=true ready=true, sin tocar launcher, perfil ni runtime key.

**E2E VALIDADO:** se creó y conectó el segundo plugin LoomLCITestHTTP. Las 25 herramientas aparecen en ChatGPT, WorkSession y Filesystem funcionan, pero tres process_run concurrentes por HTTP tardaron 13,017 s vs. 12,403 s secuenciales. No hubo mejora de paralelismo E2E; ver [[A3.2 - Benchmark ChatGPT HTTP]]. El Host HTTP local sí es concurrente, por lo que resta aislar el escalonamiento en la ruta remota.
