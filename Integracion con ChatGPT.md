# Integración con ChatGPT

> Estado: integración local preparada; limitación de superficie encontrada en Chat normal.

## Configuración realizada

ChatGPT Desktop tiene registrado el servidor MCP local LoomLCI por STDIO:

- comando: `C:\Program Files\dotnet\dotnet.exe`
- argumento: DLL de `LoomLCI.Host`
- working directory: raíz del proyecto LoomLCI

La configuración aparece activa en Ajustes → Complementos → MCP.

## Plugin local

También se creó un plugin versionable dentro del repo:

- `plugin/.codex-plugin/plugin.json`
- `plugin/.mcp.json`
- `plugin/skills/loomlci/SKILL.md`

Se agregó al marketplace personal y se instaló como `loomlci@personal` versión 0.1.0.

En Chat normal, `@LoomLCI` aparece correctamente y carga la skill.

## Smoke test realizado

Se pidió desde una conversación Chat normal:

1. crear WorkSession
2. iniciar `cmd.exe`
3. ejecutar un comando con exit code 7
4. consultar estado
5. leer stdout/stderr
6. cerrar WorkSession

Resultado observado:

- ChatGPT reconoció el plugin/skill.
- No recibió las tools MCP de LoomLCI en esa conversación.
- No se inició `LoomLCI.Host`.
- ChatGPT no simuló la ejecución y reportó correctamente que las tools no estaban disponibles.

Antes del plugin, registrar sólo el servidor MCP tampoco hacía disponibles sus tools en Chat normal.

## Conclusión

El servidor LoomLCI no es el problema: el round-trip MCP por STDIO ya está validado mediante el cliente oficial en los tests de integración.

La limitación observada está en la superficie actual de ChatGPT Desktop: Chat normal puede cargar el plugin/skill, pero en esta versión/cuenta no expone las tools del MCP local. La documentación oficial actual usa ChatGPT Work para probar plugins con servidores MCP locales.

No se debe acoplar LoomLCI a Work por esta limitación. Loom sigue siendo MCP genérico y se puede probar end-to-end con Work de forma controlada si se desea.

## Fuentes

- https://developers.openai.com/plugins/quickstart
- https://developers.openai.com/plugins/build/plugins
- https://developers.openai.com/plugins/deploy/connect-chatgpt
- https://help.openai.com/en/articles/20001256-plugins-in-chatgpt
