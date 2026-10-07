# Plugin metadata y verificación

> Estado: **CERRADO end-to-end (2026-10-06).** Producto / Deployment 4 quedó implementado: fuente canónica versionada, export del contrato MCP vivo, snapshot verificable, build reproducible del plugin y migración real del plugin privado a 0.3.0.

## Objetivo

Evitar que la metadata/skill del plugin se vuelva a separar del contrato MCP real, sin duplicar dentro de la skill todos los schemas y detalles operativos de las tools.

La separación fijada es:

- **MCP vivo**: fuente de verdad de nombres, titles, descriptions, annotations y schemas;
- **skill**: workflow humano y reglas de selección/uso;
- **plugin manifest**: identidad/activación/UI;
- **app `LoomLCI MCP` + Secure MCP Tunnel**: única conexión MCP objetivo.

El plugin ya no debe arrancar un Host local ni depender del repo o de outputs Debug.

## Fuente canónica

El repo conserva bajo `plugin/`:

- `plugin/plugin.json`: manifest portable canónico y única fuente de versión;
- `plugin/README.md`: arquitectura/requisitos actuales;
- `plugin/skills/loomlci/SKILL.md`: workflow;
- `plugin/contract/mcp-contract.json`: snapshot normalizado del contrato MCP publicado por un Host Release.

No se versionan manualmente copias de `.codex-plugin/plugin.json`, `mcp.json` o `.mcp.json`: se generan al empaquetar.

## Export del contrato MCP

`scripts/Export-McpContract.ps1`:

1. arranca un `LoomLCI.Host.exe` publicado;
2. negocia MCP por STDIO con `initialize`;
3. envía `notifications/initialized`;
4. pagina `tools/list`;
5. ordena tools por nombre;
6. exporta:
   - protocol version;
   - server info;
   - ServerInstructions;
   - name/title/description;
   - input/output schemas;
   - annotations.

No parsea C# ni mantiene una lista paralela de tools.

## Build del plugin

`scripts/Build-PluginPackage.ps1`:

1. publica un Host Release `win-x64` temporal salvo que se pase `HostPath`;
2. exporta el contrato MCP real;
3. compara byte a byte con `plugin/contract/mcp-contract.json`;
4. falla ante drift salvo uso explícito de `-UpdateContractSnapshot`;
5. valida la fuente canónica;
6. genera compatibilidad:
   - `.codex-plugin/plugin.json` desde `plugin.json`;
   - `mcp.json` con `mcpServers: {}`;
   - `.mcp.json` con `mcpServers: {}`;
7. valida nuevamente el staging;
8. crea ZIP con nombres internos portables usando `/`.

Los dos archivos MCP vacíos son **neutralizadores de migración**: Plugin Creator no permite borrar archivos al actualizar una release existente, por lo que sobrescriben el wiring STDIO obsoleto de 0.2.x sin volver a declarar un servidor.

## Verificación

`scripts/Verify-PluginPackage.ps1` comprueba:

- manifest y versión válidos;
- `plugin.json` sin `mcpServers`;
- snapshot MCP no vacío y sin nombres duplicados;
- toda tool mencionada explícitamente por la skill existe en el catálogo vivo;
- ausencia de rutas `C:\Users\...`, `bin\Debug`, `LoomLCI.Host.dll` y wiring de desarrollo;
- manifest de compatibilidad sincronizado con el canónico;
- neutralizadores MCP realmente vacíos.

IntegrationTests agregan regresión sobre:

- snapshot vs catálogo anunciado por el Host;
- title/description de todas las tools;
- referencias explícitas de la skill;
- ausencia de wiring Debug/rutas locales en la fuente canónica.

## Plugin privado 0.3.0

Migración aplicada sobre el mismo plugin privado:

- plugin id: `plugins_6ac0a247c2b08191bca02893456adf28`;
- versión anterior: `0.2.1`;
- versión actual: `0.3.0`;
- release actual: `pluginrel_6ac58cb3a20c8191b25dc92c7dfe2e08`;
- update realizado con compare-and-swap sobre la release 0.2.1.

El read-back confirmó:

- descripción actualizada para Filesystem + Process + Python + Visual + Work Plan;
- skill actualizada con `process_run`, Python y Visual Files;
- sin ruta fija al repo;
- `.codex-plugin/plugin.json` generado y sincronizado;
- `mcp.json` / `.mcp.json` con `mcpServers: {}`;
- ningún wiring al Host Debug.

Durante la primera publicación Plugin Creator rechazó el ZIP porque `ZipFile.CreateFromDirectory` produjo entradas con `\` en Windows. El builder se corrigió para generar entry names con `/`; el segundo paquete fue aceptado sin modificar el contrato.

## Validación

- build normal del plugin sin actualizar snapshot: **OK**;
- snapshot exportado: **24 tools**, coincide con Host Release;
- package verifier fuente + staging: **OK**;
- ZIP final: **4.159 bytes**;
- SHA-256 del ZIP publicado: `90fb1a2d4faefb2a233814ececabc08746d5fb24e8b3f6eb50863859506c02f3`;
- suite Release: **285/285**;
- IntegrationTests: **17/17**;
- Plugin Creator update CAS: **OK**;
- read-back de 0.3.0: **OK**.

## Workflow futuro

Cuando cambie el contrato MCP:

1. implementar/validar la capability;
2. ejecutar el build del plugin;
3. si detecta drift, revisar el diff del contrato;
4. actualizar la skill sólo si cambió el workflow que realmente necesita enseñar;
5. aceptar el snapshot explícitamente con `-UpdateContractSnapshot`;
6. bump de versión en un solo lugar: `plugin/plugin.json`;
7. generar ZIP;
8. actualizar plugin con CAS;
9. hacer read-back y refresh/smoke en ChatGPT.

La **reconciliación final** de la skill sigue diferida hasta terminar las nuevas capabilities Python, tal como define [[Roadmap post-G1]]. El mecanismo para evitar drift ya queda cerrado.
