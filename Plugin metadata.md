# Plugin metadata y verificación

> Estado: **CERRADO end-to-end (2026-10-07).** Mecanismo anti-drift y reconciliación final post-Python 0.4.0 completados: fuente canónica, contrato MCP, build/verifier, publicación CAS/read-back y evaluación fresh-agent 4/4 validados.

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

El mecanismo general para evitar drift queda cerrado; la reconciliación final post-Python se diseña a continuación.

## Reconciliación final post-Python — investigación/diseño

> Estado: **CERRADO end-to-end. Implementación, publicación 0.4.0 y evaluación fresh-agent 4/4 completadas.**

### Drift confirmado

El plugin privado publicado sigue en **0.3.0** y está detrás de la fuente canónica: la skill publicada todavía no enseña el workflow de paquetes de Python 1 y tampoco conoce el bridge privado Python 2 (loom.fs / loom.process).

El snapshot canónico MCP ya está actualizado a Python 2 y coincide byte a byte con el Host instalado 0.1.0-dev-python2: **25 tools**, SHA-256 `674e3edc6f209dbf15074b08ace8484cb7c67c122aa343d73a8df842d86e6064`. Por lo tanto esta reconciliación no debe tocar ServerInstructions/tool descriptions ni actualizar el snapshot salvo drift inesperado.

### Cambios recomendados

- Skill: conservar WorkSession, Work Plan, Filesystem, Visual Files y Process top-level; agregar explícitamente python_packages_prepare/python_reset y enseñar que dentro de python_execute se prefieren loom.fs y loom.process cuando el workflow Python necesite filesystem/process de la misma WorkSession. No volver a entrar por MCP/tunnel desde el worker Python.
- Skill: mantener visuales top-level para imagen/render PDF y Process top-level cuando se requiera explícitamente un proceso Independent.
- README: agregar una nota breve explicando que loom.fs/loom.process son API privada del worker Python y no nuevas tools MCP públicas.
- Manifest: mantener description/keywords/UI/defaultPrompt; sólo bump de versión.

Versión recomendada: **0.4.0**. El cambio incorpora una capacidad/workflow material nuevo y además absorbe drift acumulado desde 0.3.0; 0.3.1 comunicaría un patch menor cuando cambia de forma significativa la estrategia recomendada del agente.

### Anti-drift adicional

El verifier actual valida sólo referencias a tools MCP públicas. Como loom.fs y loom.process no son tools MCP, hoy podría volver a perderse ese guidance sin que CI falle.

Agregar una regresión explícita que exija en la skill final: python_execute, python_packages_prepare, python_reset, loom.fs, loom.process, visuales top-level y la excepción de Process top-level para Independent. Conviene reforzarlo con un IntegrationTest dedicado además del verifier.

### Publicación propuesta

1. actualizar SKILL.md y README;
2. bump único de plugin/plugin.json: 0.3.0 -> 0.4.0;
3. agregar verifier/test semántico;
4. focused tests + Integration;
5. Build-PluginPackage.ps1 sin UpdateContractSnapshot; debe confirmar 25 tools y snapshot sin drift;
6. justo antes de publicar, releer current_release_id;
7. actualizar el mismo plugin privado mediante CAS;
8. read-back completo de 0.4.0, incluidos neutralizadores mcp.json/.mcp.json vacíos;
9. refresh y fresh-agent evaluation.

### Resultado de implementación/publicación 0.4.0

Implementado:

- `plugin/plugin.json`: **0.3.0 -> 0.4.0**;
- skill reconciliada con:
  - `python_packages_prepare` / `python_reset`;
  - `loom.fs` / `loom.process` dentro de `python_execute`;
  - prohibición de reentrada MCP/tunnel desde el worker;
  - visuales top-level para imagen/render PDF;
  - Process top-level para procesos `Independent`;
- README actualizado para distinguir bridge privado de tools MCP públicas;
- verifier extendido con markers del workflow Python final;
- IntegrationTest `PluginSkillTracksFinalPythonWorkflow`.

Validación:

- verifier fuente: **OK**, 0.4.0 / 25 tools / 10 tool refs explícitas;
- tests de plugin focalizados: **3/3**;
- IntegrationTests completos: **20/20**;
- suite Release serial completa: **338/338**;
- build del plugin contra Host instalado Python 2, sin actualizar snapshot: **OK**;
- snapshot MCP: **25 tools**, sin drift;
- ZIP: **4.639 bytes**;
- SHA-256: `304a984ab0adab29032b529374593e33e80c32d9e917e4b799cd905562ddd36c`.

Publicación:

- plugin id preservado: `plugins_6ac0a247c2b08191bca02893456adf28`;
- release anterior: `pluginrel_6ac58cb3a20c8191b25dc92c7dfe2e08` (0.3.0);
- release actual: `pluginrel_6ac5e253c70081918df6046afb0572fa` (0.4.0);
- update por compare-and-swap: **OK**;
- read-back completo: **OK**;
- `.codex-plugin/plugin.json`: 0.4.0 sincronizado;
- `mcp.json` / `.mcp.json`: `mcpServers: {}` preservado;
- no se creó un plugin nuevo ni se modificó el wiring del tunnel.

La primera llamada a Plugin Creator con una ruta Windows fue rechazada antes de mutar el plugin; el ZIP se trasladó al entorno de Plugin Creator conservando tamaño/hash y el segundo intento CAS fue exitoso.

### Fresh-agent final

**Caso 1 — bridge Python autónomo: PASS.** Un chat nuevo, sin mencionar `loom.fs`/`loom.process`, eligió `work_create` + `python_execute` + `work_close` y, dentro del worker, `loom.fs.find_paths(...)` + `loom.process.run(...)`. No usó `filesystem_*`/`process_*` top-level para esas operaciones. Confirmó persistencia real del worker entre dos `python_execute` mediante un global conservado.

Asperezas observadas, no bloqueantes:

- una llamada fue bloqueada por la plataforma antes de llegar a LoomLCI; la repetición mínima funcionó;
- para descubrir firmas exactas de `loom.fs`/`loom.process`, el agente recurrió a `inspect.signature`;
- los dicts devueltos son correctos pero algo verbosos para usos donde sólo interesan pocos campos.

Follow-up futuro recomendado: mejorar autodocumentación/discoverability in-worker de `loom.*` sin cambiar la arquitectura ni ampliar la superficie MCP pública. No bloquea la reconciliación 0.4.0.

**Caso 2 — filesystem simple top-level: PASS.** Un chat nuevo leyó `global.json` usando `work_create` + `filesystem_read_files` + `work_close`, devolvió correctamente `.NET SDK 10.0.400` y no introdujo Python/bridge ni escritura innecesaria.

**Caso 3 — visual top-level: PASS.** Un chat nuevo inspeccionó `visual-smoke.png` mediante `filesystem_view_image`, describió correctamente el contenido visual y no introdujo Python/`loom.fs` ni operaciones de escritura.

**Caso 4 — proceso `Independent` top-level: PASS.** Un chat nuevo creó una WorkSession, inició `powershell.exe -NoProfile -Command Start-Sleep -Seconds 30` con ownership `Independent` mediante Process top-level, cerró la WorkSession, confirmó que el proceso seguía `running`, luego ejecutó `process_terminate` y `process_release`. No usó `loom.process` ni modificó archivos.

**Evaluación fresh-agent final: 4/4 PASS.** La skill 0.4.0 selecciona correctamente bridge privado dentro de Python cuando corresponde y conserva Filesystem/Visual/Process top-level para los casos donde son la superficie adecuada. No se observó sobreuso de Python/bridge ni regresión que justifique repetir el benchmark completo de Work Plan.

### Reconciliación Python 3 - plugin 0.5.0

Implementado/publicado en P3.2:

- manifest canónico: **0.4.0 -> 0.5.0**;
- skill y README incorporan `loom.display_image` sólo para imágenes generadas completamente en memoria por Python;
- imagen local existente conserva `filesystem_view_image`;
- página PDF existente conserva `filesystem_render_pdf_page`;
- verifier e IntegrationTest protegen los tres markers y la distinción in-memory vs archivo local;
- verifier fuente: **OK**, 25 tools / 12 refs explícitas;
- suite Release posterior: **356/356**;
- build del plugin contra Host instalado `0.1.0-dev-python3`: **OK, sin drift MCP**;
- paquete: **4.896 bytes**, SHA-256 `7d608a2e3cbcdc55a4eb76e0eea8ce1f573c118ebed6f5683312e1f08fd844ad`;
- plugin id preservado: `plugins_6ac0a247c2b08191bca02893456adf28`;
- release anterior: `pluginrel_6ac5e253c70081918df6046afb0572fa` (0.4.0);
- release actual: `pluginrel_6ac66682d9908191b16d4033923f84fa` (0.5.0);
- update CAS: **OK**;
- read-back de manifest/skill/README/neutralizadores: **OK**;
- `.codex-plugin/plugin.json`: 0.5.0 sincronizado;
- `mcp.json` / `.mcp.json`: `mcpServers: {}` preservado.

La ruta Windows volvió a ser rechazada por Plugin Creator antes de mutar el plugin; trasladar el mismo ZIP al entorno de Plugin Creator resolvió el upload sin cambiar el contenido.

Pendiente para cerrar Python 3 end-to-end: fresh-agent visual en chat nuevo, porque el chat que ejecutó el cutover conserva el snapshot anterior de `python_execute`.

### Criterio de cierre

La reconciliación queda cerrada cuando la fuente canónica refleja Python 1 + Python 2, el anti-drift protege loom.fs/loom.process, el paquete 0.4.0 verifica 25 tools sin drift, el plugin existente se actualiza por CAS/read-back y los fresh-agent positives/negatives seleccionan correctamente bridge vs tools top-level.
