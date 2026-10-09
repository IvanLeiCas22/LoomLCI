# Estructura del repositorio v0.1

> Estado: **baseline de repositorio adoptada y reconciliada con el árbol real al 2026-10-04**. La separación Core / Windows / MCP / Host y los tres proyectos de tests están implementados. Python Runtime E1 y Agent Support F1 están cerrados; Computer sigue pendiente.

## Criterio

Separar dependencias arquitectónicas reales sin crear un proyecto .NET por cada carpeta o capability desde el día uno.

Regla principal:

    Core no depende de Windows ni de MCP.

## Estructura actual

    LoomLCI/
    ├─ src/
    │  ├─ LoomLCI.Core/
    │  │  ├─ AgentSupport/
    │  │  ├─ Filesystem/
    │  │  ├─ Invocations/
    │  │  ├─ Lifetime/
    │  │  ├─ Observability/
    │  │  ├─ Process/
    │  │  ├─ Python/
    │  │  ├─ Resources/
    │  │  ├─ VisualFiles/
    │  │  └─ Work/
    │  ├─ LoomLCI.Windows/
    │  │  ├─ Filesystem/
    │  │  ├─ Process/
    │  │  ├─ Python/
    │  │  └─ VisualFiles/
    │  ├─ LoomLCI.Mcp/
    │  └─ LoomLCI.Host/
    ├─ runtime/
    │  └─ python/
    │     ├─ worker.py
    │     ├─ runtime.json
    │     └─ README.md
    ├─ tests/
    │  ├─ LoomLCI.Core.Tests/
    │  ├─ LoomLCI.Mcp.Tests/
    │  ├─ LoomLCI.Windows.Tests/
    │  └─ LoomLCI.IntegrationTests/
    ├─ .obsidian/
    ├─ *.md
    └─ ...

`LoomLCI.Core/Python` contiene contratos/capability E1.1. `LoomLCI.Windows/Python` contiene protocolo E1.2 y assets/provisioner/provider/resource E1.3. `LoomLCI.Mcp/PythonTools.cs` expone `python_execute`/`python_reset` desde E1.4. `runtime/python` conserva las fuentes versionadas del worker y manifiesto. `LoomLCI.Core/AgentSupport` contiene contratos/capability Work Plan F1.1 y `WorkSession.WorkPlan.cs` conserva su estado session-local; `LoomLCI.Mcp/WorkPlanTools.cs` expone F1.2 de forma opt-in. `LoomLCI.Mcp.Tests` cubre desde G1.0 el resultado MCP mixto y sus límites visuales. G1.1 agregó `Core/VisualFiles`, `Windows/VisualFiles` y `Mcp/VisualFilesTools.cs`; F1.3 quedó validado por túnel/fresh-agent y Computer sigue pendiente.

## LoomLCI.Core

Pure .NET.

Contiene actualmente:

- WorkSession model/registry;
- Handle/Resource registry;
- Invocation lifecycle y cancellation composition;
- capabilities/contratos de Filesystem y Process;
- contratos y capability Core de Python Runtime E1.1;
- contratos/capability Work Plan de Agent Support F1.1;
- lifetime/expiry;
- result/error model;
- event contracts/bus.

Diferido:

- ExecutionContext/Policy explícitos;
- contratos de Computer.

No contiene:

- MCP SDK
- Win32
- UI Automation
- ConPTY
- Windows.Graphics.Capture
- Python executable details

Debe poder probarse completamente con fake providers.

## LoomLCI.Windows

Implementación Windows concreta de las capabilities actuales.

Módulos implementados:

    Filesystem/
    Process/
    Python/

Incluye hoy:

- Win32/CsWin32;
- Job Objects;
- ConPTY;
- filesystem Windows semantics;
- protocolo/framing y Named Pipe privado de Python Runtime E1.2;
- assets embebidos, auto-provisioning privado, provider y worker resource de Python Runtime E1.3.

El Host registra `IPythonRuntimeProvider`/`PythonCapability` y E1.4 expone `PythonTools` por MCP STDIO con structuredContent/outputSchema.

Computer, UI Automation, Windows.Graphics.Capture, SendInput y una abstracción formal HostFullTrustExecutionContext siguen diferidos.

Depende de Core.

Core nunca depende de Windows.

## LoomLCI.Mcp

Adapter MCP.

Responsabilidades:

- MCP SDK
- tool definitions
- DTO/schema mapping
- structuredContent/outputSchema
- error mapping
- cancellation mapping;
- transporte STDIO;
- compatibilidad de versión.

Streamable HTTP sigue diferido. Secure MCP Tunnel es transporte externo al runtime y no agrega una segunda implementación MCP dentro de LoomLCI.

Depende de Core.

No contiene lógica de recursos.

## LoomLCI.Host

Composition root / executable.

Responsabilidades:

- DI/config
- elegir ExecutionContext
- registrar providers/capabilities
- iniciar adapters
- lifecycle/shutdown
- configuración local

Depende de Core + Windows + Mcp.

## runtime/python

Worker Python separado.

Ejemplo futuro:

    runtime/python/
    ├─ worker.py
    ├─ protocol.py
    ├─ release_inputs.py
    └─ requirements/lock...

El controller C# puede empezar dentro de Windows o Host y moverse a un proyecto LoomLCI.Python cuando se implemente. No crear ese assembly antes de necesitarlo.

## Tests

### Core.Tests

Tests rápidos, sin Windows real:

- WorkSession lifecycle
- Handle registry
- expiry
- type mismatch
- cancellation
- errors
- Work Plan revision/invariants
- event correlation

### Windows.Tests

Integración controlada con Windows:

- process spawn
- process tree cleanup
- stdout/stderr
- cancellation
- ConPTY más adelante
- filesystem semantics
- Computer más adelante

### IntegrationTests

Cruzan boundaries:

- MCP -> Core -> Windows
- structured output
- handle threading
- cancellation
- shutdown/cleanup
- Python cuando exista

## Dependency graph

    LoomLCI.Host
       |-- LoomLCI.Core
       |-- LoomLCI.Windows -> LoomLCI.Core
       -- LoomLCI.Mcp     -> LoomLCI.Core

No se permiten dependencias Windows <-> Mcp.

## Git / Obsidian

La raíz será simultáneamente bóveda Obsidian y repositorio Git.

Antes de escribir código:

1. inicializar Git en la raíz de LoomLCI
2. crear `.gitignore`
3. excluir configuración local/sensible de Obsidian; en particular nunca versionar credenciales del Local REST API
4. hacer un commit inicial con la arquitectura/documentación base
5. recién después crear la solución y comenzar el Milestone 1

Por seguridad, la propuesta inicial es ignorar `.obsidian/` completa. Más adelante se puede versionar selectivamente configuración no sensible si aporta valor.
