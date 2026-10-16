# FuriousAntiCheat

Mod para Valheim y BepInEx que valida los plugins declarados por los clientes al conectarse a un servidor. Versión actual: **2.3.2**; identificador: `com.furiousvalheim.anticheat`.

## Funcionamiento

Al iniciar, obtiene los plugins cargados por BepInEx y calcula el SHA-256 de sus DLL. El servidor construye un catálogo a partir de DLL de referencia y solicita al cliente su lista de GUID, versiones y hashes mediante RPC. Las huellas se guardan en memoria; es necesario reiniciar después de cambiar DLL o catálogos.

- Los mods obligatorios deben estar presentes con la misma versión y el mismo SHA-256.
- Los opcionales pueden faltar; si están instalados, deben coincidir exactamente.
- Cualquier plugin fuera del catálogo se considera no autorizado.
- FuriousAntiCheat se agrega automáticamente a los obligatorios, usando la DLL cargada por el servidor.
- En modo `Enforce`, se rechazan las discrepancias, respuestas inválidas y clientes que agoten el tiempo de espera. Los clientes sin este mod también agotan ese plazo.
- En modo `AuditOnly`, estos rechazos se registran y se permite la conexión.

El cliente recibe un motivo detallado antes de la desconexión y lo muestra en la pantalla de error de conexión. El servidor espera una confirmación hasta 1,5 segundos y deja 0,35 segundos adicionales para enviar el estado de expulsión. Los mensajes se limitan a 800 caracteres y se limpian de etiquetas y caracteres de control.

## Requisitos

- Valheim y una instalación funcional de BepInEx compatible con el juego, tanto en servidor como en clientes.
- Las dependencias de BepInEx/Harmony y Mono.Cecil provistas por esa instalación.
- La misma DLL de FuriousAntiCheat en ambos extremos.
- Para compilar: .NET SDK compatible con proyectos `net48`, referencias de .NET Framework 4.8 y los ensamblados locales del juego y BepInEx indicados en el proyecto.

No se fija una versión concreta de Valheim o BepInEx en el proyecto. La compatibilidad debe comprobarse contra la instalación utilizada para compilar; las actualizaciones del juego pueden cambiar métodos o campos utilizados por los parches.

## Instalación y catálogo del servidor

1. Copiar `FuriousAntiCheat.dll` en `BepInEx/plugins/FuriousAntiCheat/` en el servidor y en cada cliente.
2. Iniciar una vez para generar la configuración y las carpetas, y detener el juego o servidor.
3. En el servidor, copiar las DLL de referencia de los mods permitidos a estas carpetas:

```text
BepInEx/
  config/
    com.furiousvalheim.anticheat.cfg
    FuriousAntiCheat/
      ModsObligatorios/
      ModsOpcionales/
  plugins/
    FuriousAntiCheat/
      FuriousAntiCheat.dll
```

4. Reiniciar y revisar `BepInEx/LogOutput.log` para confirmar `manifest-valid=True`.

Las DLL del catálogo son referencias de validación: colocarlas en `config` no instala ni ejecuta esos mods. Instalar los plugins que deban ejecutarse en `BepInEx/plugins`. El catálogo puede incluir mods exclusivos del cliente sin cargarlos en el servidor. Solo se leen DLL del primer nivel de cada carpeta, sin subcarpetas; deben contener metadatos `BepInPlugin`.

Un catálogo vacío permite únicamente FuriousAntiCheat. No se agregan automáticamente todos los demás mods del servidor. Un GUID presente en ambas carpetas, errores en los obligatorios, ausencia de la huella del propio mod o un catálogo de más de 100 plugins invalidan el manifiesto. Los errores en DLL opcionales se registran y esas entradas se omiten; por tanto, un cliente que las declare puede ser rechazado por mod no autorizado.

## Configuración

Archivo: `BepInEx/config/com.furiousvalheim.anticheat.cfg`. Editar con el proceso detenido y reiniciar.

| Sección | Clave | Predeterminado | Efecto |
|---|---|---|---|
| `1 - General` | `Enabled` | `true` | Activa la validación; `false` desactiva comprobaciones y expulsiones del mod. |
| `1 - General` | `EnforcementMode` | `Enforce` | `Enforce` aplica rechazos; `AuditOnly` registra los rechazos normales. |
| `1 - General` | `KickTimeoutSeconds` | `15` | Espera por intento; valor finito de al menos 1 segundo. |
| `1 - General` | `HandshakeRetries` | `1` | Reintentos adicionales; entre 0 y 5. |
| `2 - Strictness` | `MaxDisplayedProblems` | `6` | Motivos mostrados por rechazo; entre 1 y 50. |
| `3 - Feedback` | `LogAcceptedConnections` | `false` | Registra las validaciones satisfactorias. |

Los valores numéricos inválidos vuelven a sus valores predeterminados. Con la configuración inicial hay dos intentos de aproximadamente 15 segundos antes del rechazo por falta de respuesta, más el envío de la desconexión. `MaxDisplayedProblems` limita la presentación, no la cantidad de verificaciones.

## Compilación

```powershell
dotnet build FuriousAntiCheat.csproj -c Release -p:ValheimLibPath="D:\SteamLibrary\steamapps\common\Valheim"
```

Cambiar la ruta según la instalación. El proyecto referencia `BepInEx.dll`, `BepInEx.Harmony.dll`, `0Harmony20.dll`, `Mono.Cecil.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `assembly_valheim.dll` y `assembly_utils.dll`. Las dependencias no se incluyen en la DLL del mod. Con la configuración actual, la salida habitual es `bin/Release/net48/net48/FuriousAntiCheat.dll`.

## Diagnóstico y comprobación manual

- **Falta un mod:** instalarlo en el cliente y comprobar su GUID y versión.
- **Versión o hash diferente:** distribuir exactamente la DLL usada como referencia y reiniciar ambos extremos; una recompilación puede cambiar el hash sin cambiar la versión.
- **Mod no autorizado:** retirarlo del cliente o agregar su DLL al catálogo correspondiente y reiniciar el servidor.
- **Sin respuesta:** comprobar instalación, `Enabled`, compatibilidad y registros; ajustar el plazo para clientes lentos.
- **Manifiesto inválido:** buscar `Invalid mod manifest` en el registro y corregir los errores antes de habilitar `Enforce`.

Antes de usarlo en producción, probar una conexión válida, un obligatorio ausente, un opcional ausente, una DLL distinta, un plugin extra y un cliente sin el mod. Revisar también `AuditOnly`. La compilación por sí sola no verifica la integración dentro de Valheim.

## Alcance y limitaciones

Este mod controla la coherencia de los plugins declarados; no garantiza la ausencia de trampas. La lista y los hashes son reportados por el cliente y un cliente manipulado puede falsificarlos. No detecta por sí mismo inyección, modificaciones en memoria, herramientas externas, archivos de configuración alterados ni comportamiento indebido durante la partida. Solo toma una instantánea de los plugins cargados al inicio y utiliza el protocolo de intercambio versión 2, con un máximo de 100 entradas.

Los fallos de inicialización desactivan el mod para proteger el proceso del juego. Algunas excepciones de validación desconectan directamente incluso fuera del flujo normal de auditoría. Supervisar los registros: `AuditOnly` no garantiza tolerancia ante todos los errores internos.

## Releases

La versión se mantiene en `Plugin.cs` y `FuriousAntiCheat.csproj`. Para esta versión corresponde el tag `v2.3.2`, con `FuriousAntiCheat.dll` como archivo descargable del release. Los ensamblados de Valheim y BepInEx no deben adjuntarse al repositorio ni al release.
