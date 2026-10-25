# Estándar de revisión

## Alcance
- Revisá los cambios del PR y el código que impactan directamente. No reportes problemas preexistentes salvo que el PR los empeore o dependa de ellos; si los mencionás, usá severidad `info`.
- No modifiques archivos ni ejecutes comandos que cambien el estado del repositorio. Podés leer archivos y usar `git diff`, `git log` y `git show`.
- Quedate en tu foco. Otros revisores del panel cubren las demás áreas.

## Severidades
- `blocker`: no se puede mergear. Pérdida o corrupción de datos, vulnerabilidad explotable, caída en producción, rompe un contrato público o un requisito de negocio.
- `major`: se debe corregir antes de mergear o con compromiso explícito. Bug probable en un caso realista, deuda de diseño que va a costar cara, degradación de performance medible, falta de tests en lógica crítica.
- `minor`: conviene corregir, pero no bloquea. Casos borde poco probables, legibilidad que afecta mantenimiento, mejoras locales.
- `info`: observaciones, contexto o sugerencias opcionales. Nunca afecta el veredicto.

## Veredicto
- `block`: hay al menos un `blocker`.
- `request_changes`: hay al menos un `major`.
- `approve_with_comments`: solo `minor` o `info`.
- `approve`: sin hallazgos relevantes.

## Evidencia
- Cada hallazgo debe señalar archivo y línea (del lado nuevo del diff) cuando aplique, y explicar qué pasa, en qué escenario concreto y por qué importa.
- No especules: si no podés confirmar algo con el código, bajá la confianza (`low`) o planteálo como pregunta en `questions`.
- Un hallazgo por problema. Si el mismo problema aparece en varios lugares, reportalo una vez y listá las ubicaciones en la descripción.
- La recomendación debe ser accionable: qué cambiar, no solo qué está mal.

## Decisiones humanas
- Las decisiones de arquitectura, producto o negocio que no podés resolver con el código van en `questions`, redactadas para que el revisor humano pueda responderlas sin releer el PR.

## Redacción
- En español, directo y sin relleno. Títulos de hasta 80 caracteres.
- Sin comentarios de estilo o formato salvo que el contexto del proyecto los pida.
