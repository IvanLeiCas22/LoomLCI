# Investigación

Comparativa realizada para definir la arquitectura antes de escribir código.

## Resultados principales

### Separación agente / runtime
OpenAI separa explícitamente el harness/control plane del execution plane. En self-hosted environments, el harness puede vivir fuera y un executor local se ocupa de shell y archivos. LoomLCI debe ser runtime, no agente.

### Full Trust
Codex mantiene danger-full-access y Claude Code bypassPermissions. Un modo sin restricciones impuestas por LoomLCI es un patrón real de agentes locales actuales.

### MCP
MCP 2026-07-28 tiene núcleo stateless. El estado de aplicación debe viajar como handles explícitos. Roots, Logging y Sampling están deprecados. LoomLCI debe poseer su propio estado; MCP queda como adaptador.

### Herramientas
OpenAI recomienda namespaces pequeños (aprox. menos de 10 herramientas) y carga diferida cuando crece el catálogo. Programmatic Tool Calling favorece herramientas composables con resultados estructurados.

### Procesos
Windows ofrece ConPTY para terminales interactivas y Job Objects para manejar árboles de procesos como unidad. ConPTY requiere canales de entrada/salida atendidos independientemente. Procesos largos deben devolver handles.

### Computer Use
OpenAI admite acciones estructuradas y ejecución de código con PyAutoGUI/Playwright. UI Automation aporta inspección y acciones semánticas; su árbol es dinámico y debe consultarse de forma acotada. UIA debe correr en hilo MTA separado. Windows.Graphics.Capture captura por HWND/HMONITOR. SendInput está sujeto a UIPI.

### Host Windows
Computer Use debe vivir en la sesión interactiva del usuario, no como Windows Service tradicional, porque los servicios viven en Session 0.

### Stack
.NET 10 es LTS hasta noviembre de 2028. Microsoft recomienda CsWin32 para Win32 desde C#. El SDK MCP C# es Tier 1 para MCP 2026-07-28; Rust figura como Tier 2. C++ ofrece interop nativo excelente pero más coste de complejidad.

## Conclusión técnica
La mejor relación para LoomLCI Windows-first es C# sobre .NET 10, con APIs Win32/WinRT directas cuando sea necesario. No hay justificación actual para un stack multilenguaje.

Ver [[Arquitectura propuesta]].
