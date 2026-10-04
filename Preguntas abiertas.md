# Preguntas abiertas

## Arquitectura general

- Baseline v0.1 reconciliada y aceptada en [[Arquitectura propuesta]], [[Especificacion interna v0.1]] y [[Estructura del repositorio v0.1]].
- Mantener explícita la distinción entre arquitectura implementada y capabilities futuras.

## Python Runtime

El vertical slice E1 quedó diseñado en [[Bloque E - Python Runtime]]. Cerrado para E1: runtime privado CPython 3.14.8 embeddable, Named Pipe versionado, un worker lazy por WorkSession, timeout/reset y stdlib-only.

Preguntas deliberadamente diferidas:

- paquetes de terceros posteriores a E1;
- forma exacta del futuro bridge `loom.*`;
- outputs de imagen cuando la capa MCP utilizada tenga una ruta binaria estable;
- integración PyAutoGUI/Computer.

## Computer

- Contrato de Observation y stale detection.
- Captura multi-monitor/DPI.
- Relación exacta entre UIA actions, input físico y Python/PyAutoGUI.

## Agent Support

- F1.1 Core implementado/validado: estado Work Plan dentro de WorkSession, revision/CAS, límites y lifecycle.
- F1.2 implementado/validado: `work_plan_get`/`work_plan_update`, schema/annotations y opt-in estático por adapter.
- F1.3 preflight + smoke por túnel aprobados con catálogo 19 tools; pendientes 3 fresh-agents no triviales y control trivial.
- Evaluar más adelante si hacen falta dependencias explícitas/DAG; no incluirlas en v0.1 sin evidencia.

## Operación

- WorkSession idle TTL, tombstone retention, ProcessHandle post-exit TTL y explicit release definidos en [[Bloque D0 - Resource lifetime y expiry]]; quedan futuras policies por nuevos resource kinds.
- tamaños de buffers.
- formato y retención del audit durable.
- autenticación remota si se habilita HTTP fuera de localhost/tunnel.
- elevación futura.
