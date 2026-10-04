# Preguntas abiertas

## Arquitectura general

- Baseline v0.1 reconciliada y aceptada en [[Arquitectura propuesta]], [[Especificacion interna v0.1]] y [[Estructura del repositorio v0.1]].
- Mantener explícita la distinción entre arquitectura implementada y capabilities futuras.

## Python Runtime

- Paquetes incluidos por defecto.
- Protocolo IPC worker ↔ Core.
- Forma exacta del futuro bridge loom.*.
- Política concreta de timeout/reset.

## Computer

- Contrato de Observation y stale detection.
- Captura multi-monitor/DPI.
- Relación exacta entre UIA actions, input físico y Python/PyAutoGUI.

## Agent Support

- Nombre público y schema del Work Plan opcional.
- Evaluar más adelante si hacen falta dependencias explícitas/DAG; no incluirlas en v0.1 sin evidencia.
- Política de exposición según host/adaptador.

## Operación

- WorkSession idle TTL, tombstone retention, ProcessHandle post-exit TTL y explicit release definidos en [[Bloque D0 - Resource lifetime y expiry]]; quedan futuras policies por nuevos resource kinds.
- tamaños de buffers.
- formato y retención del audit durable.
- autenticación remota si se habilita HTTP fuera de localhost/tunnel.
- elevación futura.
