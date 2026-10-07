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

La validación fresh-agent de la baseline descubrió y ejercitó las **15 capabilities públicas** de Work, Filesystem y Process. Python Runtime E1 llevó el catálogo a **17 tools** y Agent Support a **19 tools**. Visual Files G1 llevó el Host a **22 tools** con Work Plan habilitado. `process_run` elevó el catálogo a **23 tools**, [[Ergonomía - Work Plan patch]] a **24 tools** y [[Python 1 - Paquetes administrados]] agrega `python_packages_prepare`, por lo que el Host instalado actual expone **25 tools**. F1.3 cerró previamente con el benchmark real-world final 4/4 positivos, 3/3 controles simples y escalada correcta 8A sin plan -> 8B con plan. Tras el cutover de Python 1, ChatGPT mantuvo inicialmente un catálogo cacheado de 24 tools; el refresco manual actualizó correctamente a 25 y habilitó el smoke directo de `python_packages_prepare`.

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

## Deployment instalado activo

[[Deployment portable]] sigue siendo la base del payload y [[Instalador Windows]] es ahora el camino de instalación convencional:

- Host Release self-contained instalado fuera del repo;
- `LoomLCI.Launcher` con Start/Stop/Status/Setup y accesos directos de iniciar/detener;
- tunnel-client v0.0.14 oficial, fijado y verificado por hash;
- profile/state/secrets aislados por máquina bajo `%LOCALAPPDATA%\LoomLCI\deployment`;
- setup EXE per-user registrado en Aplicaciones instaladas;
- uninstall real validado;
- cutover y rollback del runtime validados;
- smoke real desde ChatGPT validado.

El runtime activo final es `loomlci-installed`, actualmente en `0.1.0-dev-python2` (sequence 0), healthy/ready sobre el mismo tunnel, con `0.1.0-dev-python1` conservado como rollback. El installer real quedó registrado como `LoomLCI` con uninstall bajo `%LOCALAPPDATA%\Programs\LoomLCI\unins000.exe`. [[Auto-update firmado]] fue validado contra una GitHub Release pública real y mantiene `highestSequence=3`; los milestones Python de desarrollo se instalaron side-by-side con sequence 0 sin consumir nuevas secuencias públicas.

Visual Files G1, `filesystem_read_files`, `process_run`, [[Ergonomía - Work Plan patch]], Python 1 y Python 2 están incluidos. [[Plugin metadata]] dejó el plugin privado en **0.4.0** como capa de workflow/metadata reconciliada: la app `LoomLCI MCP` + Secure MCP Tunnel sigue siendo la única conexión MCP, los viejos `mcp.json/.mcp.json` permanecen neutralizados con `mcpServers: {}`, y la skill final enseña `python_packages_prepare`/`python_reset` más el bridge privado `loom.fs`/`loom.process` sin convertirlos en tools MCP públicas. La suite Release actual es **338/338**, IntegrationTests **20/20**, el catálogo público permanece en **25 tools** con Work Plan habilitado y la evaluación fresh-agent de la skill 0.4.0 pasó **4/4**.

## Notas

- Work/Codex puede consumir MCP local por otros mecanismos, pero no es parte del camino objetivo.
- El objetivo principal es que ChatGPT normal pueda trabajar con la PC local.
- LoomLCI permanece agnóstico de ChatGPT: sigue siendo un runtime MCP local; el túnel y la app son adaptadores externos.
