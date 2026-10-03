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

## Smoke test

Desde ChatGPT normal, usando “Probar ahora” sobre `LoomLCI MCP`, ChatGPT pudo invocar herramientas reales de LoomLCI a través del túnel.

Tools observadas:

- `work_create`
- `process_start`
- `process_status`
- `process_read`
- `work_close`

Esto valida la cadena completa entre ChatGPT normal y procesos reales de Windows.

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
