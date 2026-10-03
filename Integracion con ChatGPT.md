# Integración con ChatGPT

> Estado: integración local por STDIO validada end-to-end en ChatGPT Desktop Work.

## Configuración

ChatGPT Desktop tiene registrado el servidor MCP local LoomLCI por STDIO:

- comando: `C:\Program Files\dotnet\dotnet.exe`
- argumento: DLL de `LoomLCI.Host`
- working directory: raíz del proyecto LoomLCI

También existe un plugin local versionado dentro del repo:

- `plugin/.codex-plugin/plugin.json`
- `plugin/.mcp.json`
- `plugin/skills/loomlci/SKILL.md`

El plugin está agregado al marketplace personal e instalado como `loomlci@personal` versión 0.1.0.

## Superficies probadas

### Chat normal

`@LoomLCI` aparece y carga la skill, pero en la prueba realizada Chat normal no expuso las tools MCP locales.

Esto no invalida el servidor ni el plugin; simplemente esa superficie no fue suficiente para ejecutar las tools en esta versión/cuenta.

### Work

Prueba realizada en una conversación nueva, fuera del proyecto LoomLCI:

1. cambiar a Work
2. seleccionar `@LoomLCI`
3. crear WorkSession
4. iniciar proceso
5. consultar estado
6. leer stdout/stderr con cursores
7. cerrar WorkSession

Resultado: integración end-to-end exitosa.

ChatGPT Work arrancó `LoomLCI.Host` automáticamente mediante el MCP local del plugin y usó las tools de LoomLCI.

El log de ChatGPT mostró `server=loomlci status=ready`.

## Smoke test definitivo

Para evitar ambigüedades de quoting de `cmd.exe`, se usó:

- executable: `powershell.exe`
- arguments:
  - `-NoProfile`
  - `-Command`
  - `Write-Output 'loom-chatgpt-smoke'; exit 7`

Resultado:

- exit code: `7`
- stdout: `loom-chatgpt-smoke\r\n`
- stderr: vacío
- lectura por cursores: correcta
- WorkSession: cerrada correctamente

Tools usadas por ChatGPT Work:

- `work_create`
- `process_start`
- `process_status`
- `process_read` (dos llamadas)
- `work_close`

Esto valida la cadena completa:

    ChatGPT Work
        -> plugin @LoomLCI
        -> MCP STDIO local
        -> LoomLCI.Host
        -> ProcessCapability
        -> Windows
        -> proceso real

## Conclusión

No hace falta agregar Streamable HTTP ni Secure MCP Tunnel para la integración local con ChatGPT Desktop Work.

El servidor STDIO actual ya es suficiente para el camino local:

    ChatGPT Desktop Work
        -> plugin local
        -> .mcp.json
        -> dotnet.exe LoomLCI.Host.dll
        -> STDIO

Secure MCP Tunnel sigue siendo una opción futura para superficies cloud/web donde OpenAI necesite alcanzar un runtime que corre localmente, pero no es requisito para el flujo local validado.

LoomLCI debe seguir siendo agnóstico del host: Work es sólo una de las superficies que hoy puede consumir correctamente el MCP local.

## Fuentes

- https://developers.openai.com/plugins/quickstart
- https://developers.openai.com/plugins/build/plugins
- https://developers.openai.com/plugins/deploy/connect-chatgpt
- https://help.openai.com/en/articles/20001256-plugins-in-chatgpt
