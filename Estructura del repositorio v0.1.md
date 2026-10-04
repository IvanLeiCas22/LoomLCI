# Estructura del repositorio v0.1

> Estado: **baseline de repositorio adoptada y reconciliada con el árbol real al 2026-10-04**. La separación Core / Windows / MCP / Host y los tres proyectos de tests están implementados. Python Runtime E1.1 ya existe en Core; worker/IPC, Agent Support y Computer siguen pendientes.

## Criterio

Separar dependencias arquitectónicas reales sin crear un proyecto .NET por cada carpeta o capability desde el día uno.

Regla principal:

    Core no depende de Windows ni de MCP.

## Estructura actual

    LoomLCI/
    ├─ src/
    │  ├─ LoomLCI.Core/
    │  │  ├─ Filesystem/
    │  │  ├─ Invocations/
    │  │  ├─ Lifetime/
    │  │  ├─ Observability/
    │  │  ├─ Process/
    │  │  ├─ Python/
    │  │  ├─ Resources/
    │  │  └─ Work/
    │  ├─ LoomLCI.Windows/
    │  │  ├─ Filesystem/
    │  │  └─ Process/
    │  ├─ LoomLCI.Mcp/
    │  └─ LoomLCI.Host/
    ├─ tests/
    │  ├─ LoomLCI.Core.Tests/
    │  ├─ LoomLCI.Windows.Tests/
    │  └─ LoomLCI.IntegrationTests/
    ├─ .obsidian/
    ├─ *.md
    └─ ...

`LoomLCI.Core/Python` ya existe con contratos y capability E1.1. Todavía no existen `runtime/python`, backend Windows de Python, Computer ni AgentSupport. Cuando se implementen, deben respetar esta misma dirección de dependencias; no hace falta crear assemblies separados por capability salvo que aparezca una razón concreta.

## LoomLCI.Core

Pure .NET.

Contiene actualmente:

- WorkSession model/registry;
- Handle/Resource registry;
- Invocation lifecycle y cancellation composition;
- capabilities/contratos de Filesystem y Process;
- contratos y capability Core de Python Runtime E1.1;
- lifetime/expiry;
- result/error model;
- event contracts/bus.

Diferido:

- ExecutionContext/Policy explícitos;
- Work Plan / Agent Support;
- contratos de Computer;
- worker/IPC y backend Windows/MCP de Python Runtime.

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

Incluye hoy:

- Win32/CsWin32;
- Job Objects;
- ConPTY;
- filesystem Windows semantics.

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
