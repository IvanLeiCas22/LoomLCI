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
- runtime activo instalado: alias `loomlci-installed`
- profile/state: `%LOCALAPPDATA%\LoomLCI\deployment`
- tunnel-client instalado: `%LOCALAPPDATA%\Programs\LoomLCI\tools\tunnel-client.exe`
- runtime legacy `loomlci`: conservado pero detenido para rollback

El runtime instalado se opera normalmente mediante `LoomLCI.Launcher`, que delega el lifecycle local en `tunnel-client runtimes ...`.

Comandos útiles:

```powershell
& "$env:LOCALAPPDATA\Programs\LoomLCI\LoomLCI.Launcher.exe" status
& "$env:LOCALAPPDATA\Programs\LoomLCI\LoomLCI.Launcher.exe" start
& "$env:LOCALAPPDATA\Programs\LoomLCI\LoomLCI.Launcher.exe" stop
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

La validación fresh-agent de la baseline descubrió y ejercitó las **15 capabilities públicas** de Work, Filesystem y Process. Python Runtime E1 llevó el catálogo a **17 tools** y Agent Support a **19 tools**. Visual Files G1.1 agregó `filesystem_view_image` y llevó el Host a 20 tools; G1.2 agregó `filesystem_read_pdf`, por lo que el Host actual expone **21 tools** con Work Plan habilitado. F1.3 cerró previamente con el benchmark real-world final 4/4 positivos, 3/3 controles simples y escalada correcta 8A sin plan -> 8B con plan.

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

## Deployment portable activo

[[Deployment portable]] quedó implementado y validado end-to-end en esta PC:

- Host Release self-contained instalado fuera del repo;
- `LoomLCI.Launcher` con Start/Stop/Status/Setup y acceso directo de escritorio;
- tunnel-client v0.0.14 oficial, fijado y verificado por hash, independiente de IvanSpace;
- profile/state/secrets aislados por máquina bajo `%LOCALAPPDATA%\LoomLCI\deployment`;
- cutover legacy -> instalado validado;
- rollback instalado -> legacy validado;
- segundo cutover al instalado validado;
- smoke real desde ChatGPT en ambos caminos validado.

El runtime activo final es `loomlci-installed`, actualmente en `0.1.0-dev-63d256e9b658`; el legacy `loomlci` permanece detenido como fallback. La misma instalación portable fue además validada desde cero en una segunda PC Windows x64: ChatGPT operó sobre la notebook, luego se detuvo ese runtime y se volvió a iniciar LoomLCI en la PC de escritorio sobre el mismo tunnel, confirmando el cambio de máquina correctamente.

Visual Files G1.1, G1.2 y G1.3 ya están instalados en el runtime portable. G1.1 confirmó que `filesystem_view_image` es invocable pero ChatGPT normal no materializa el `ImageContentBlock` como entrada visual; se registra como **BLOCKED_UPSTREAM / client compatibility**. G1.2 agregó `filesystem_read_pdf` y completó su smoke directo con catálogo de 21 tools. G1.3 agregó `filesystem_render_pdf_page`; el runtime nuevo pasó el smoke ChatGPT -> app -> tunnel con `work_create` + `work_close`, pero esta conversación conserva el catálogo de 21 acciones cargado antes del upgrade. Un chat refrescado debe descubrir la tool 22 para el smoke directo final; la limitación visual upstream de G1.1 seguirá aplicando al `ImageContentBlock`.

## Notas

- Work/Codex puede consumir MCP local por otros mecanismos, pero no es parte del camino objetivo.
- El objetivo principal es que ChatGPT normal pueda trabajar con la PC local.
- LoomLCI permanece agnóstico de ChatGPT: sigue siendo un runtime MCP local; el túnel y la app son adaptadores externos.
