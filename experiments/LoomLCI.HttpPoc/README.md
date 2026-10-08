# A3.1 — Host MCP Streamable HTTP aislado

Esta es una PoC **experimental**, fuera de LoomLCI.slnx y del instalador/Launcher. No sustituye el runtime STDIO en producción.

## Contrato técnico

- Modelo: Microsoft.NET.Sdk.Web (.NET 10) + ModelContextProtocol.AspNetCore 2.2.0.
- Escucha IPv4 loopback 127.0.0.1 y puerto definido en LOOMLCI_HTTP_POC_PORT (1024–65535).
- Arranque exige LOOMLCI_HTTP_POC_TOKEN (secreto aleatorio de al menos 32 bytes UTF-8); rechaza solicitudes sin Bearer o con Bearer erróneo.
- Cada ruta valida Host=127.0.0.1:puerto y rechaza Origin; sin CORS. Todos los endpoints funcionales requieren Bearer (comparación SHA-256 en tiempo constante). **Única excepción:** los dos GET de /.well-known/oauth-protected-resource[/mcp] responden 404 sin token, para indicar ausencia de metadata OAuth y permitir el doctor del túnel; no exponen tools ni datos de Loom.
- MCP Stateless en /mcp; WorkSession persistente dentro del Host mediante workId; /health también autenticado.
- Full Trust Windows: nunca exponer el puerto ni reutilizar la runtime API key del túnel.

## Compilar y ejecutar smoke

Desde la raíz del repo:

    dotnet build experiments/LoomLCI.HttpPoc/LoomLCI.HttpPoc.csproj -c Release
    python experiments/LoomLCI.HttpPoc/smoke.py --baseline-host "C:\ruta\Host\LoomLCI.Host.exe"

smoke.py inicia un Host aislado con secreto aleatorio en memoria y un puerto loopback dinámico; no imprime ni almacena secretos. Compara los 25 contratos MCP contra el Host STDIO indicado y verifica security, initialize, WorkSession/Work Plan, lectura, 3 process_run secuenciales y 3 concurrentes, work_close y terminación. El parámetro --baseline-host es opcional, pero se recomienda para validar paridad de contrato.

## Validación 2026-10-08

- Build Release: 0 errores y 0 advertencias.
- Suite completa de LoomLCI Release: **366/366 PASS** (`dotnet test LoomLCI.slnx -c Release --no-restore`).
- Arranque sin token: falla cerrado.
- Tests de acceso: 401 sin token o token incorrecto; 403 por Host falso o Origin; 200 con credenciales correctas.
- HTTP initialize MCP 2025-11-25, tools/list: 25 herramientas.
- Catálogo igual al Host Release 5 STDIO: 0 diferencias de nombres, descripciones, títulos, esquemas o anotaciones.
- WorkPlan y WorkSession conservados entre múltiples requests HTTP.
- Benchmark sleep 1,6 s × 3: secuencial 5,547 s; paralelo 1,867 s, todos exitCode=0.
- Smoke automatizado sleep 1,2 s × 3: secuencial 4,345 s; paralelo 1,494 s, PASS.
- work_close OK; proceso experimental detenido; runtime de ChatGPT original sin cambios.

## A3.2: preflight HTTP con tunnel-client, sin OpenAI remoto

    python experiments/LoomLCI.HttpPoc/tunnel_preflight.py --tunnel-client "C:\ruta\tunnel-client.exe"

El script usa un Host HTTP protegido, token y puerto aleatorios, perfil/estado aislados en carpeta temporal, tunnel ID sintético y runtime key deliberadamente ficticia. Activa referencias de encabezados locales mediante env:, ejecuta tunnel-client init y doctor, y detiene/limpia todo al terminar.

**Validado**: preflight de config, host HTTP, metadata OAuth opcional y listener de health: PASS. **No validado**: registro real del túnel en OpenAI Platform, autenticación de runtime remoto, polling, plugin ChatGPT ni paralelismo completo desde ChatGPT. Un doctor exitoso con key ficticia no prueba esas partes.

Para E2E hace falta crear un túnel real adicional con workspace asociado y runtime key restringida, registrar un segundo plugin, y comprobar el recorrido remoto, sin reutilizar el ID ni la clave productivos.

## Fuera de alcance

No hay aún integración con Secure MCP Tunnel HTTP, ni integración de start/stop, rollback, estado o autenticación persistente en Launcher. Las 25 herramientas conservan sus clases, pero PDF, Python e imágenes necesitarían pruebas específicas antes de un cutover. Para probar conexión con ChatGPT, crear un túnel de test distinto al de producción con sus propias credenciales. No inferir rendimiento end-to-end a partir de este benchmark local.
