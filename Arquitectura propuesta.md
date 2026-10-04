# Arquitectura base v0.1

> Estado: **confirmada como baseline y reconciliada con la implementación al 2026-10-04**. Process, Filesystem, Job Objects/ConPTY, lifecycle D0 y Python Runtime E1 están implementados y validados end-to-end. Agent Support F1.1 Core + F1.2 MCP están implementados/validados; F1.3 y Computer siguen pendientes.

## Principios
1. LoomLCI es un runtime local, no un agente.
2. Full Trust es el modo principal: no impone restricciones adicionales a la cuenta actual de Windows.
3. Full Trust no equivale a elevación administrativa.
4. MCP es adaptador externo, no modelo interno.
5. El estado duradero de ejecución usa handles explícitos.
6. Las ubicaciones conocidas son contexto, no límites de seguridad.
7. Capabilities estructuradas conviven con shell/ejecución general.
8. Todo es observable; persistencia desacoplada, opcional y acotable.

## Capas
Agent/Host → Adapter → Core → Capabilities → Windows providers → Windows

## Core

Implementado:

- Handle/Resource registry;
- WorkSession e Invocation lifecycle;
- Cancellation;
- Filesystem y Process contracts/capabilities;
- Work Plan / Agent Support Core F1.1;
- lifetime/expiry;
- event bus/observability básica.

Diferido:

- Capability registry explícito;
- ExecutionContext/Policy;
- Known Locations;
- evaluación final por túnel/fresh-agent de Agent Support F1.3.

No contiene planificación, prompts, memoria del agente ni lógica específica de un proveedor LLM.

## Capabilities iniciales

### Filesystem
Descubrimiento, búsqueda, lectura y edición estructurada. Acceso a cualquier ruta permitida por Windows. Patch como operación de primera clase; shell como fallback general.

### Process
Ejecución directa y shell. Procesos largos/interactivos devuelven handle. Pipes normales para procesos no interactivos; ConPTY cuando se necesita terminal. Job Object para árbol de procesos y cleanup.

### Computer
- enumeración de ventanas
- captura de ventana/escritorio
- inspección UI Automation acotada
- acciones de mouse/teclado
- acciones semánticas UIA cuando existan

Observaciones con IDs efímeros; las acciones visuales pueden referenciar la observación que las originó para detectar estado obsoleto y evitar errores de coordenadas/escalado.

No se propone Application separada inicialmente: abrir procesos pertenece a Process y manipular ventanas a Computer.

## Concurrencia
Filesystem y Process pueden trabajar en paralelo. El input global del escritorio se serializa. UIA corre sobre hilo MTA dedicado.

## Protocolos

Implementado:

- MCP por STDIO;
- Secure MCP Tunnel como transporte externo para ChatGPT normal.

Diferido:

- Streamable HTTP propio.

Los handles son de LoomLCI, no sesiones MCP.

## Compatibilidad con harnesses

LoomLCI no implementa Programmatic Tool Calling. Las tools públicas deben ser composables y tener schemas/resultados estructurados para que un harness superior pueda usarlas tanto mediante llamadas directas como mediante PTC u otros mecanismos equivalentes.

## Stack recomendado
- C# / .NET 10 LTS
- SDK oficial MCP C# 2.x
- CsWin32
- WinRT / Windows.Graphics.Capture
- UI Automation COM
- async / CancellationToken / Channels

Helper nativo sólo si una limitación medida lo exige.

## Primera versión
Un único proceso de usuario en la sesión interactiva de Windows. Sin Windows Service, sandbox, daemon privilegiado ni UI propia obligatoria.

Orden actual: Process vertical slice ✓ → Filesystem ✓ → Job Objects/ConPTY ✓ → lifecycle D0 ✓ → cierre de baseline/fresh-agent → Python Runtime → Agent Support → Computer → evaluaciones.
