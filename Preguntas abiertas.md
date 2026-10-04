# Preguntas abiertas

## Arquitectura general

- Revisar/aceptar [[Especificacion interna v0.1]].
- Revisar/aceptar [[Estructura del repositorio v0.1]].
- Definir nombres públicos exactos sólo cuando se implemente el primer slice.

## Process

- Contrato público exacto de run/start/read/write/status/terminate.
- Semántica de ConPTY y cursores del stream terminal: contrato, backend y lifecycle implementados en [[Bloque C2 - ConPTY]]; pendiente validación final de C2.4.
- Política concreta de TTL/retención de ProcessHandle después del exit.

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

- TTLs concretos.
- tamaños de buffers.
- formato y retención del audit durable.
- autenticación remota si se habilita HTTP fuera de localhost/tunnel.
- elevación futura.
