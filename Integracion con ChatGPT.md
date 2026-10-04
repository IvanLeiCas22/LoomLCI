# Integración con ChatGPT

> Estado: integración de LoomLCI con ChatGPT normal validada end-to-end mediante Secure MCP Tunnel.

## Arquitectura actual

```text
ChatGPT normal
    -> app MCP "LoomLCI MCP"
    -> Secure MCP Tunnel
    -> tunnel-client local
    -> LoomLCI.Host por STDIO
    -> capacidades LoomLCI
    -> Windows
```

LoomLCI sigue exponiendo MCP por STDIO local. El túnel es sólo el transporte seguro entre ChatGPT y ese servidor local.

## Túnel

- nombre remoto: `LoomLCI`
- tunnel id: `tunnel_6ac0b25408088191bd552887eda2a0f7`
- runtime local administrado: alias `loomlci`
- perfil local: `loomlci`

El runtime se gestiona con `tunnel-client runtimes connect`, no como un proceso temporal de una sesión de agente.

Comandos útiles:

```powershell
tunnel-client runtimes status loomlci --json
tunnel-client runtimes stop loomlci
```

## App en ChatGPT

La app instalada se llama `LoomLCI MCP`.

Configuración validada:

- conexión: Tunnel
- tunnel id: `tunnel_6ac0b25408088191bd552887eda2a0f7`
- autenticación: sin autenticación
- estado: conectada

No se necesita un plugin local `loomlci@personal`, un plugin cloud adicional ni un registro manual `mcp_servers.loomlci`.

## Validación desde ChatGPT normal

La integración fue validada primero con smoke tests y finalmente con una prueba fresh-agent integral en un chat nuevo.

La validación fresh-agent de la baseline descubrió y ejercitó las **15 capabilities públicas** de Work, Filesystem y Process. Python Runtime E1 agregó `python_execute` y `python_reset`, llevando el catálogo a **17 tools**. Agent Support agregó `work_plan_get` y `work_plan_update`, llevando el catálogo a **19 tools**. F1.3 pasó smoke por Secure MCP Tunnel. La adopción natural evolucionó de 0/3 a 1/3 tras reforzar ServerInstructions/descriptions y finalmente a **2/3 positivos con 2/2 controles negativos** tras actualizar la skill del plugin a 0.2.1; Agent Support quedó cerrado sin cambios adicionales de Core/API.

Ver [[Validacion final fresh-agent]], [[Bloque E - Python Runtime]] y [[Bloque F - Agent Support]].

### Semántica de errores

LoomLCI conserva los errores de negocio como `CallToolResult` con `IsError=true` y `structuredContent` del tipo:

```text
{ ok: false, error: { code, message, ... } }
```

ChatGPT/túnel puede mostrar exteriormente esos tool errors mediante un wrapper como `INVALID_ARGUMENT` / `RuntimeException`. El código/mensaje semántico de LoomLCI sigue estando presente y el agente puede recuperarse. Esto se considera una característica de presentación del consumidor, no un bug del runtime LoomLCI.

## Configuración descartada

Durante la investigación se probaron rutas que ya no forman parte de la instalación final:

- registro manual `[mcp_servers.loomlci]` en la configuración local de Codex/Desktop
- plugin local `loomlci@personal`
- plugin cloud anterior de LoomLCI
- paquete versionado `plugin/` con `.mcp.json` STDIO

Esas rutas producían registros duplicados o no exponían las tools a Chat normal.

La instalación final mantiene una única ruta de acceso: la app MCP conectada al Secure MCP Tunnel.

## Notas

- Work/Codex puede consumir MCP local por otros mecanismos, pero no es parte del camino objetivo.
- El objetivo principal es que ChatGPT normal pueda trabajar con la PC local.
- LoomLCI permanece agnóstico de ChatGPT: sigue siendo un runtime MCP local; el túnel y la app son adaptadores externos.
