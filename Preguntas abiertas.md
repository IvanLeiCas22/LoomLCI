# Preguntas abiertas

## Arquitectura general

- Revisar/aceptar [[Especificacion interna v0.1]].
- Revisar/aceptar [[Estructura del repositorio v0.1]].
- Definir nombres públicos exactos sólo cuando se implemente el primer slice.

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
- Reasociar el proceso vivo del Secure MCP Tunnel con la metadata del supervisor `runtimes`: el perfil/túnel está `live/ready`, pero el alias local todavía registra el runtime como `stopped` después de que la reconexión administrada fuera bloqueada por la capa de seguridad del entorno.
- tamaños de buffers.
- formato y retención del audit durable.
- autenticación remota si se habilita HTTP fuera de localhost/tunnel.
- elevación futura.
