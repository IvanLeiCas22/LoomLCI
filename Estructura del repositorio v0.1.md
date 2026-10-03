# Estructura del repositorio v0.1

> Estado: propuesta para el inicio del código.

## Criterio

Separar dependencias arquitectónicas reales sin crear un proyecto .NET por cada carpeta o capability desde el día uno.

Regla principal:

    Core no depende de Windows ni de MCP.

## Estructura inicial

    LoomLCI/
    ├─ src/
    │  ├─ LoomLCI.Core/
    │  ├─ LoomLCI.Windows/
    │  ├─ LoomLCI.Mcp/
    │  └─ LoomLCI.Host/
    │
    ├─ runtime/
    │  └─ python/
    │
    ├─ tests/
    │  ├─ LoomLCI.Core.Tests/
    │  ├─ LoomLCI.Windows.Tests/
    │  └─ LoomLCI.IntegrationTests/
    │
    ├─ .obsidian/
    ├─ *.md
    └─ ...

No crear todavía proyectos separados para Filesystem, Process, Computer o AgentSupport. Primero mantenerlos como módulos/namespaces internos y dividir assemblies sólo si las dependencias o tamaño lo justifican.

## LoomLCI.Core

Pure .NET.

Contiene:

- WorkSession model/registry
- Handle/Resource registry
- Invocation lifecycle
- Cancellation composition
- Capability abstractions
- ExecutionContext abstraction
- result/error model
- event contracts/bus abstraction
- Work Plan model
- metadata/annotations internas

No contiene:

- MCP SDK
- Win32
- UI Automation
- ConPTY
- Windows.Graphics.Capture
- Python executable details

Debe poder probarse completamente con fake providers.

## LoomLCI.Windows

Implementación del HostFullTrustExecutionContext y providers Windows.

Módulos internos iniciales:

    Filesystem/
    Process/
    Computer/
    Interop/

Incluye cuando corresponda:

- Win32/CsWin32
- Job Objects
- ConPTY
- UI Automation
- Windows.Graphics.Capture
- SendInput
- filesystem Windows semantics

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
- task/cancellation mapping
- transport stdio/HTTP
- compatibilidad de versión

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
