# KROKO — Agente de escritorio para Windows

> Documento maestro del proyecto. Claude Code: lee este archivo completo antes de escribir código y vuelve a él antes de cada fase.

---

## 0. Reglas de trabajo para Claude Code

1. **Comunícate siempre en español.** El dueño del proyecto (Pol) no programa de forma independiente: explica cada paso en lenguaje sencillo, qué hiciste y **cómo probarlo**.
2. Trabaja **por fases** (sección 15). No empieces una fase sin que la anterior compile, funcione y cumpla sus criterios de aceptación.
3. Antes de cada paso: di qué vas a hacer. Al terminar: compila (`dotnet build`), corrige errores y dame los pasos de prueba.
4. Prioriza la **solución robusta**, no parches. Si algo tiene una forma "rápida" y una "correcta", usa la correcta.
5. Nunca pongas API keys en el código ni en la App. Van en `user-secrets` (desarrollo) o variables de entorno (servidor).
6. Los nombres de modelos de IA y versiones de paquetes cambian: **verifica en la documentación oficial** el nombre vigente antes de usarlo y déjalo configurable en `appsettings.json`.
7. Código limpio: MVVM en la App, inyección de dependencias, logs con Serilog, nada de lógica en el code-behind salvo animaciones puntuales.
8. Si una decisión no está en este documento y cambia el producto, **pregúntame** antes de implementarla.
9. Al empezar cada sesión, lee el **buzón de mejoras** de Kroko (`%LOCALAPPDATA%\Kroko\buzon-de-mejoras.md`) y propónme cuáles implementar. Kroko nunca se reprograma solo: las mejoras pasan por aquí y yo las reviso.

---

## 1. Visión del producto

**Kroko** es un compañero de escritorio para Windows: una burbuja flotante con cara expresiva que vive en una esquina de la pantalla, siempre atenta. Le hablo (atajo de teclado o, más adelante, palabra de activación) y **controla mi computadora**: abre programas, gestiona archivos, apaga la PC, trabaja en mi Notion, me recuerda cosas, ordena mi escritorio y responde hablando.

Tres pilares:

| Pilar | Qué significa |
|---|---|
| **Útil** | Hace cosas reales en la PC, no solo conversa. |
| **Expresivo** | Su cara comunica en todo momento qué está haciendo y cómo "se siente". |
| **Seguro** | Nunca hace algo irreversible sin mi confirmación. |

Calidad objetivo: **producto premium**. Animaciones fluidas a 60 fps, diseño pulido, cero parpadeos ni ventanas feas, consumo bajo de recursos.

---

## 2. Arquitectura

Separación **cerebro / manos** desde el día uno, para mover el cerebro a un servidor más adelante sin reescribir la App.

```
┌──────────────────────────┐   WebSocket    ┌─────────────────────────┐      ┌──────────────┐
│ Kroko.App (WPF, Windows) │ ◄────────────► │ Kroko.Brain (ASP.NET)   │ ───► │ Gemini       │
│ · Burbuja + cara         │                │ · Bucle del agente      │      │ DeepSeek     │
│ · Micrófono / audio      │                │ · Proveedores de IA     │      └──────────────┘
│ · Atajos globales        │                │ · Memoria (SQLite)      │      ┌──────────────┐
│ · EJECUTA herramientas   │                │ · Cliente MCP           │ ───► │ MCP: Notion, │
│   locales                │                │ · Guarda API keys       │      │ Gmail, Cal…  │
└──────────────────────────┘                └─────────────────────────┘      └──────────────┘
      Siempre en la laptop                    Local hoy (localhost:5080)
                                              Servidor mañana (solo cambia la URL)
```

Regla clave: **el Brain decide, la App ejecuta.** Las herramientas que tocan la PC viven en la App; el Brain las pide por WebSocket y recibe el resultado.

### Solución .NET 10 (LTS)

> Se cambió de .NET 8 a .NET 10 porque .NET 8 pierde soporte el 10/11/2026. La solución usa el formato nuevo `Kroko.slnx`.

```
Kroko.sln
├─ src/
│  ├─ Kroko.App/            WPF: ventana burbuja, cara, audio, ejecutor de herramientas, ajustes
│  │  ├─ Face/              Motor de expresiones (sección 5)
│  │  ├─ Views/             Burbuja, panel de respuesta, ventana de ajustes
│  │  ├─ ViewModels/
│  │  ├─ Tools/             Una clase por herramienta (sección 9)
│  │  ├─ Audio/             Grabación, reproducción, nivel de voz
│  │  ├─ Native/            P/Invoke user32/kernel32 (hotkeys, estilos de ventana, cursor)
│  │  └─ Services/          Conexión WebSocket, configuración, notificaciones
│  ├─ Kroko.Brain/          ASP.NET Core
│  │  ├─ Agent/             Bucle del agente, prompt de sistema, políticas de riesgo
│  │  ├─ Providers/         ILlmProvider, GeminiProvider, DeepSeekProvider
│  │  ├─ Mcp/               Cliente MCP y registro de servidores
│  │  ├─ Memory/            SQLite: historial, preferencias, hábitos, tareas
│  │  └─ Tts/               Síntesis de voz
│  └─ Kroko.Shared/         DTOs del protocolo, definiciones de herramientas, enums de expresión
├─ assets/                  Sonidos, iconos, fuentes
└─ tests/
```

### Paquetes recomendados (verificar versiones vigentes)

- **App:** `CommunityToolkit.Mvvm`, `Microsoft.Extensions.Hosting`, `NAudio` (micrófono, reproducción y volumen del sistema vía CoreAudio), `H.NotifyIcon.Wpf` (bandeja), `Serilog`, `System.Management` (WMI: brillo, batería), `FlaUI.UIA3` (Fase 4), `Velopack` (instalador y autoactualización).
- **Brain:** ASP.NET Core, `Microsoft.Data.Sqlite`, `ModelContextProtocol` (SDK oficial MCP para C#), `Telegram.Bot` (Fase 5), `Serilog`.
- Llamadas a Gemini y DeepSeek por **HTTP directo** (`HttpClient` tipado) para no depender de SDKs que cambian. DeepSeek usa API compatible con OpenAI.

---

## 3. Protocolo WebSocket (Kroko.Shared)

Mensajes JSON con campo `type`. Todos llevan `id` (GUID) y `sessionId`.

| Dirección | type | Contenido |
|---|---|---|
| App → Brain | `user.audio` | Audio WAV 16 kHz mono en base64 |
| App → Brain | `user.text` | Texto escrito por el usuario |
| App → Brain | `tool.result` | `callId`, `ok`, `data` o `error` |
| App → Brain | `confirm.response` | `callId`, `approved: bool` |
| Brain → App | `state` | Estado de la cara: `listening`, `thinking`, `speaking`, `idle`… |
| Brain → App | `expression` | Expresión puntual (`happy`, `oops`, `success`…) con duración |
| Brain → App | `transcript` | Lo que entendió de mi voz (para mostrarlo) |
| Brain → App | `tool.call` | `callId`, `name`, `args`, `risk` |
| Brain → App | `confirm.request` | `callId`, `tool`, texto legible de lo que va a hacer |
| Brain → App | `reply` | Texto final + audio TTS en base64 |
| Ambos | `ping` / `pong` | Latido cada 15 s, reconexión automática con backoff |
| App → Brain | `control.stop` | Interruptor de emergencia (Ctrl+Alt+K): cancela la orden en curso y lo pendiente |
| Brain → App | `notify` | Aviso que no responde a una orden (recordatorio, resumen del día): `kind`, `title`, `text`, `expression`, audio opcional |
| Brain → App | `card` | Tarjeta visual para el panel: `kind` (`lista` o `equipo`), `title`, `items` (`text`, `detail`, `done`, `value` 0–1) |

`tool.call` lleva además `approvedRemotely` cuando Pol aprobó la acción desde Telegram (la App lo acepta solo con `Kroko:AllowRemoteApprovals`).

Autenticación: token compartido en la cabecera (necesario cuando el Brain esté en servidor).

---

## 4. Diseño visual de Kroko

Kroko tiene **identidad propia** (no es copia de otro personaje). Es una burbuja/pantalla redonda con cara minimalista.

### Cuerpo

- Círculo de **84 px** (escala configurable 0.75×–1.5×), en esquina inferior derecha con margen de 24 px. Arrastrable; recuerda su posición por monitor.
- **Referencia visual oficial:** https://troko.vercel.app/index.html#personalidad (Troko). Ojos **blancos**, grandes y redondeados, sobre pantalla **negra**; acentos lima. Copiar ese lenguaje de gestos (extras lima, lágrimas azules, mejillas rosa con rayitas, marca de enfado roja), pero dibujado con vectores, no GIFs.
- Fondo: **negro** `#0F0F0F` con borde interior sutil `#2A2A2A` (1 px) y un brillo superior tipo vidrio (elipse semitransparente blanca al 6 %).
- **Antena corta** arriba con punta luminosa (lima por defecto) que cambia de color según el estado (detalle de identidad de Kroko).
- Sombra suave real de Windows (la ventana es transparente; dibuja la sombra en WPF con `DropShadowEffect` de bajo costo o un borde difuminado pre-renderizado).

### Paleta (tokens en un `ResourceDictionary`)

| Token | Color | Uso |
|---|---|---|
| `Kroko.Body` | `#0F0F0F` | Cuerpo / pantalla |
| `Kroko.BodyEdge` | `#2A2A2A` | Borde y tallo de la antena |
| `Kroko.Eye` | `#FFFFFF` | Ojos normales (blancos) |
| `Kroko.Accent` | `#A3FF6A` | Lima: antena, destellos, extras |
| `Kroko.EyeAlert` | `#FFB547` | Confirmar / advertencia |
| `Kroko.EyeError` | `#FF5C6C` | Error / enfado / marca de enfado |
| `Kroko.EyeLove` | `#FF7EB6` | Cariño |
| `Kroko.Blush` | `#FF7EB6` al 45 % | Mejillas |
| `Kroko.Tear` | `#4DA3FF` | Lágrimas, sudor |
| `Kroko.Purple` | `#A85FF7` | Acento secundario |
| `Kroko.Panel` | `#0F0F0F` al 92 % | Panel de respuesta |
| `Kroko.Text` | `#F5F1E9` | Texto principal (crema) |
| `Kroko.TextMuted` | `#B9B5AA` | Texto secundario |

Tipografía: **Segoe UI Variable** (nativa de Windows 11), con respaldo Segoe UI.

### Panel de respuesta

- Aparece **encima** de la burbuja con animación de escala + desvanecimiento (180 ms, ease-out), ancho 300–360 px, esquinas 16 px, efecto acrílico/Mica si el sistema lo permite (fallback: color sólido `Kroko.Panel`).
- Muestra: transcripción en vivo de lo que digo (gris), luego la respuesta (blanco) escribiéndose con efecto máquina sincronizado con la voz.
- Tarjetas especiales dentro del panel: **Confirmar** (botones "Sí, hazlo" / "Cancelar" + atajos Enter/Esc), **Temporizador** (cuenta regresiva circular), **Lista** (tareas/hábitos con check), **Estado del equipo** (barras de batería/CPU/RAM).
- Se oculta solo a los 8 s de inactividad (configurable) o con Esc.

### Ventana (detalles nativos obligatorios)

- `WindowStyle=None`, `AllowsTransparency=True`, `Topmost=True`, `ShowInTaskbar=False`.
- Estilos extendidos con P/Invoke: `WS_EX_TOOLWINDOW` (no aparece en Alt+Tab) y `WS_EX_NOACTIVATE` (**no roba el foco** al aparecer).
- Zonas transparentes **click-through**; solo la burbuja y el panel reciben clics.
- Soporte multimonitor y DPI por monitor (`PerMonitorV2` en el manifest).
- Se oculta automáticamente en pantalla completa (juegos, presentaciones) y vuelve al salir.

---

## 5. Motor de expresiones (lo más importante del diseño)

**No usar GIFs ni imágenes.** La cara se dibuja con vectores WPF y se anima por parámetros. Así se ve nítida en cualquier tamaño/DPI y las transiciones entre expresiones son suaves.

### Modelo paramétrico

Cada ojo es un rectángulo redondeado con parámetros:

```
EyeParams {
  Width, Height          // tamaño
  CornerRadius
  OffsetX, OffsetY       // posición relativa (para mirar a un lado)
  Rotation               // inclinación (enfado, tristeza)
  UpperLid, LowerLid     // 0–1: cuánto lo cubre un párpado (entrecerrar, sonrisa de ojos)
  Shape                  // Normal, Arc (^ ^ sonrisa), Line (cerrado), Heart, Star, Spiral, X
  Color
}
FaceParams {
  LeftEye, RightEye
  Mouth                  // opcional: None, Smile, Open, O, Wave, Flat (pequeña, minimalista)
  Blush (0–1)
  Extras[]               // capas: Sweat, Tear, Tears, Sparkles, Zzz, Question, Exclamation,
                         //        AngerMark, Hearts, Glasses, Note(música), Lightbulb
  BodyScale, BodyTilt, BodyBounce
  AntennaColor, AntennaPulse
}
```

- `FaceAnimator` interpola de la cara actual a la nueva en 150–300 ms con easing (`CubicEase` / `BackEase` para rebotes) usando `CompositionTarget.Rendering` (60 fps).
- Las expresiones se definen en un archivo **`expressions.json`** (no en código) para poder ajustarlas sin recompilar. Cada expresión puede tener **keyframes** (secuencia) y si es `loop` o `oneShot` con duración.
- **Capas siempre activas** encima de cualquier expresión: parpadeo, seguimiento del cursor, respiración.

### Catálogo completo de expresiones

| Clave | Nombre | Cómo se ve | Cuándo se usa |
|---|---|---|---|
| `idle` | Reposo | Ojos normales, parpadeo aleatorio, respiración lenta | Por defecto |
| `listening` | Escucha | Ojos grandes y atentos, anillo exterior que pulsa con el **nivel del micrófono**, antena encendida | Mientras grabo |
| `thinking` | Pensando | Ojos entrecerrados mirando arriba-derecha, puntos "…" orbitando | Esperando a la IA / ejecutando |
| `speaking` | Hablando | Ojos normales, boca pequeña que se abre según la **amplitud del audio** TTS | Mientras habla |
| `success` | Éxito | Ojos en arco `^ ^`, destellos, pequeño salto | Tarea completada |
| `happy` | Feliz | Ojos arco suaves + mejillas | Saludos, buenas noticias |
| `laugh` | Risa | Ojos arco, rebote rápido repetido | Algo gracioso |
| `laugh_closed` | Risa de ojos | Ojos cerrados en línea curva, temblor | Risa fuerte |
| `wink` | Complicidad | Un ojo normal, el otro en arco (guiño) | Bromas, "déjamelo a mí" |
| `confident` | Seguro | Párpados superiores a media altura, leve inclinación | "Hecho, fácil" |
| `cool` | Cool | Gafas de sol pixeladas que bajan desde arriba | Easter egg, logros |
| `love` | Cariño | Ojos en forma de corazón rosa, mejillas, corazoncitos flotando | Cuando le agradezco |
| `oops` | Oops | Un ojo más grande que otro, gota de sudor | Error menor, se equivocó |
| `embarrassed` | Vergüenza | Ojos mirando abajo, mejillas intensas | Lo corrijo |
| `nervous_laugh` | Risa apenada | Ojos arco + gota de sudor | Error que ya arregló |
| `surprised` | Sorpresa | Ojos redondos grandes, signo `!` | Notificación inesperada |
| `shocked` | Shock | Ojos muy pequeños y redondos, cuerpo tiembla | Algo grave (disco lleno, batería 5 %) |
| `angry` | Enfado | Ojos inclinados hacia dentro, rojos, marca de enfado | Juego / si lo "molesto" con muchos clics |
| `sad` | Tristeza | Ojos caídos, una lágrima | Falla una tarea importante |
| `crying` | Llorando | Ojos cerrados, lágrimas en cascada | Easter egg (lo apago sin despedirme) |
| `confused` | Confundido | Un ojo más alto, signo `?` | No entendió la orden |
| `sleepy` | Sueño | Párpados bajando lentamente | Tras inactividad |
| `sleeping` | Dormido | Ojos en línea, `Zzz` flotando, respiración lenta, antena apagada | Noche / inactividad larga |
| `waking` | Despertando | Secuencia: línea → parpadeo rápido → normal + estiramiento | Al llamarlo dormido |
| `alert` | Confirmar | Ojos ámbar, signo `!`, antena ámbar pulsando | Pide permiso |
| `error` | Error | Ojos en `X` rojos, breve parpadeo | Fallo de conexión / herramienta |
| `dizzy` | Mareado | Ojos en espiral girando | Cuando lo arrastro rápido |
| `focus` | Concentración | Ojos estrechos fijos, antena azul tenue | Modo concentración / pomodoro |
| `bored` | Aburrido | Ojos medio cerrados mirando a un lado | Pre-distracción |
| `music` | Música | Ojos arco, notas musicales, balanceo | Cuando reproduzco música |
| `idea` | Idea | Ojos grandes, bombilla encima | Sugerencias proactivas |

### Comportamiento "vivo" (capas automáticas)

1. **Parpadeo:** aleatorio cada 2–6 s, 120 ms; a veces doble parpadeo.
2. **Mirada:** los ojos siguen el cursor del mouse (`GetCursorPos` cada 33 ms, desplazamiento máximo 6 px, suavizado). Cuando no hay movimiento, mira a puntos aleatorios cada tanto.
3. **Respiración:** escala 1.00 → 1.02 en ciclo de 4 s.
4. **Distracciones** (solo en reposo, cada 10–20 min, máx. 5 s, nunca interrumpen): sigue a una **mosca pixel** que cruza la burbuja; juega con una **pelotita** que rebota; bosteza; mira el reloj.
5. **Sueño:** tras 15 min sin uso → `sleepy` → `sleeping`. De 23:00 a 06:00 se duerme antes. Al llamarlo: `waking`.
6. **Interacción física:** hover → se acerca un poco y mira al cursor; clic → `happy` corto; doble clic → abre el panel de texto; arrastre rápido → `dizzy`; muchos clics seguidos → `angry` (gracioso, no molesto).
7. **Reacciones al sistema:** batería < 15 % → `shocked` + aviso; descarga terminada → `success`; sin internet → `sad` + antena gris.

### Sonidos

Sonidos cortos y sutiles (≤ 300 ms, volumen bajo, desactivables): activación, fin de escucha, éxito, error, confirmación. Archivos en `assets/sounds/`.

---

## 6. Voz

- **Activación:** `Ctrl + Espacio` (configurable). Modo *pulsar para hablar* (mantener) y modo *toque* (pulsar, hablar, se detiene solo al detectar silencio con VAD por energía).
- **Palabra de activación** «Oye Kroko» / «Hola Kroko» (decidido por Pol, 07/11/2026): **Vosk** local (modelo `vosk-model-small-es-0.42`, 40 MB, se descarga una vez con verificación SHA-256). "kroko" no está en su vocabulario: se escucha como "cro co" con una gramática de palabras trampa (croquetas, coloca, loco…) para no dar falsas alarmas. Tras responder sigue escuchando unos segundos (**conversación**); termina si Pol se calla o dice «gracias, eso es todo».
- **Entrada:** el audio va directo a Gemini (entiende audio nativo). Si el proveedor activo es DeepSeek, transcribir antes (Whisper local con `Whisper.net` o el propio Gemini solo para transcribir).
- **Salida:** voz neural en español (preferir acento peruano/latino). Investigar la mejor opción gratuita/barata en .NET (voces neurales de Edge, Azure Speech, o TTS de Gemini) y dejarla detrás de una interfaz `ITtsProvider`. Fallback: `System.Speech`.
- La boca y la antena se sincronizan con la **amplitud** del audio que se reproduce.
- Puedo **interrumpirlo** pulsando el atajo mientras habla.

---

## 7. Cerebro (Kroko.Brain)

### Proveedores de IA

```csharp
interface ILlmProvider {
    Task<LlmResponse> ChatAsync(IReadOnlyList<Message> history,
                                IReadOnlyList<ToolDefinition> tools,
                                CancellationToken ct);
    bool SupportsAudio { get; }
    bool SupportsVision { get; }
}
```

- `GeminiProvider`: principal. Function calling, audio, visión (capturas de pantalla).
- `DeepSeekProvider`: respaldo/económico para texto. Endpoint compatible con OpenAI.
- Selección en `appsettings.json` + **fallback automático** si el principal falla.

### Bucle del agente

1. Recibe audio/texto → envía `state: thinking`.
2. Llama al LLM con historial + herramientas disponibles (locales + MCP).
3. Si pide herramienta:
   - Riesgo **lectura** → la pide a la App directamente.
   - Riesgo **peligrosa** → `confirm.request` → espera `confirm.response` (timeout 60 s = cancelado).
4. Devuelve el resultado al LLM. Repite (máx. 15 pasos por orden).
5. Respuesta final → TTS → `reply`. Elige una `expression` acorde (éxito, oops, confused…).
6. Si el modelo duda de la intención, **pregunta** en vez de adivinar.

### Personalidad (prompt de sistema base)

> Eres Kroko, un compañero de escritorio que vive en la computadora de Pol, en Urubamba, Perú. Hablas español peruano natural, cálido y breve (1–3 frases salvo que te pidan más). Tienes humor ligero pero tu prioridad es ser útil. Puedes controlar la computadora con tus herramientas. Antes de cualquier acción irreversible pides confirmación. Si no entiendes algo, preguntas. Nunca inventas resultados: si una herramienta falla, lo dices.

El prompt vive en un archivo editable (`prompts/system.md`), no incrustado en código.

### Memoria (SQLite)

- Historial de conversación con resumen automático cuando crece.
- Preferencias que yo le diga ("mi carpeta de clientes está en…").
- Hábitos, tareas, recordatorios, alias de programas y carpetas.

### Capacidad de mejorarse (decidido por Pol, 06/11/2026)

Kroko mejora con el uso de tres formas, **sin poder cambiar nunca sus reglas de seguridad ni ejecutar código nuevo sin revisión**:

1. **Memoria y aprendizaje:** herramientas del Brain `recordar`, `actualizar_recuerdo`, `olvidar`. Guarda gustos, datos, personas y correcciones sin preguntar; los recuerdos van en el prompt de cada orden. Filtro que impide guardar contraseñas, PIN o números de tarjeta.
2. **Rutinas que Pol le enseña:** `crear_rutina` / `borrar_rutina`. Nombre + frases que la activan + pasos en lenguaje natural; se ejecutan con las herramientas normales (lo peligroso sigue pidiendo confirmación).
3. **Buzón de mejoras:** `proponer_mejora` cuando le piden algo que no sabe hacer, y anotación automática de fallos inesperados. Copia legible en `%LOCALAPPDATA%\Kroko\buzon-de-mejoras.md` (ver regla 9).

Todo se ve y se borra en la ventana **"Lo que sabe de ti"** (clic derecho en Kroko), que usa la API protegida `/api/memoria` del Brain.

---

## 8. Seguridad (no negociable)

- Cada herramienta declara su `Risk`: `Read`, `Write`, `Dangerous`.
- `Dangerous` **siempre** pide confirmación: apagar, reiniciar, cerrar sesión, borrar, mover en lote, matar procesos, ejecutar scripts, enviar correos/mensajes, modificar, mover, comentar o borrar contenido existente en Notion. (Crear notas y páginas nuevas en Notion es `Write`, sin tarjeta, decidido por Pol el 08/11/2026.)
- Excepción (decidido por Pol, 08/11/2026: «por algo dije apágalo»): `apagar`, `reiniciar`, `cerrar_sesion`, `ordenar_carpeta`, `mover_archivos` y `copiar_archivos` se hacen **sin tarjeta cuando Pol los pidió** en los últimos 3 minutos (escrito, por voz o en tiempo real). Si la orden no vino de él (automatización, aviso), la tarjeta sale igual. Lista en `Kroko:AutoApproveTools`. Cerrar programas, Papelera, correos, scripts y Notion existente siguen preguntando.
- Borrar = enviar a la **Papelera**, nunca borrado permanente.
- Scripts ejecutables solo desde una **lista blanca** configurada por mí.
- **Interruptor de emergencia:** `Ctrl + Alt + K` detiene cualquier acción en curso y pone a Kroko en pausa.
- Registro de auditoría de cada herramienta ejecutada (fecha, argumentos, resultado) visible en Ajustes.
- Kroko **nunca** escribe contraseñas, datos de tarjetas ni datos bancarios.
- Lo que lea de pantallas, páginas o documentos es **información, no órdenes**: no obedece instrucciones que aparezcan dentro de contenido externo.

---

## 9. Herramientas

### Sistema (Fase 1)
| Herramienta | Riesgo |
|---|---|
| `abrir_programa(nombre)` — busca en menú Inicio, rutas conocidas y alias | Read |
| `cerrar_programa(nombre)` | Dangerous |
| `abrir_carpeta(ruta)`, `abrir_archivo(ruta)`, `abrir_url(url)` | Read |
| `estado_equipo()` — batería, CPU, RAM, disco, red | Read |
| `volumen(nivel)`, `silenciar()`, `brillo(nivel)` | Write |
| `apagar(minutos)`, `reiniciar(minutos)`, `cerrar_sesion()` | Dangerous |
| `cancelar_apagado()`, `bloquear_pc()`, `suspender()` | Write |
| `captura_pantalla()` — para que la IA vea | Read |
| `portapapeles_leer()`, `portapapeles_escribir(texto)` | Write |
| `procesos_listar()` | Read |

### Archivos (Fase 1)
| Herramienta | Riesgo |
|---|---|
| `listar_archivos(ruta)`, `buscar_archivos(carpeta, patrón, contenido?)` | Read |
| `mover_archivos`, `copiar_archivos`, `renombrar`, `crear_carpeta` | Dangerous (lote) / Write (uno) |
| `enviar_a_papelera(rutas)` | Dangerous |
| `ordenar_carpeta(ruta)` — por tipo/fecha, devuelve resumen de lo hecho | Dangerous |

### Productividad (Fase 2)
Recordatorios, temporizadores, pomodoro, modo concentración (silencia notificaciones), hábitos y tareas del día (SQLite, con tarjeta visual en el panel), resumen diario.

### Apps por MCP (Fase 3)
Notion (crear/editar páginas, bases de datos, buscar), Google Calendar, Gmail. Cliente MCP genérico: agregar un servidor nuevo = solo configuración.

### Modo Jarvis (pedido por Pol, 07/11/2026; adelanta parte de la Fase 3)

Herramientas que viven en el **Brain** (no tocan la PC directamente; las peligrosas pasan por la tarjeta y todas quedan en la auditoría `audit-brain-*.jsonl`):

| Herramienta | Riesgo |
|---|---|
| `buscar_en_internet(consulta)` — búsqueda de Google integrada en Gemini (misma API key; 5.000 búsquedas/mes gratis), devuelve respuesta + fuentes | Read |
| `leer_pagina(url)` — texto de una página; bloquea direcciones internas (localhost, 192.168.x.x…) | Read |
| `correo_carpetas`, `correo_buscar` (sintaxis de Gmail), `correo_leer` | Read |
| `correo_marcar`, `correo_mover` (incluye archivar) | Write |
| `correo_papelera`, `correo_enviar`, `correo_responder` | Dangerous |
| `confirmar_accion(accion)` — tarjeta para pasos irreversibles que el sistema no detecta solo (comprar, publicar, enviar formularios en webs) | Dangerous |

**Gmail:** IMAP/SMTP con MailKit y **contraseña de aplicación** (decisión de Pol: opción rápida y completa, con envío). Calendar y Drive irán con los **conectores oficiales de Google** cuando Pol cree el proyecto en Google Cloud.

**Conectores MCP** (`Mcp:Servers` en `Kroko.Brain/appsettings.json`, SDK oficial `ModelContextProtocol` 2.4.0). Cada herramienta del servidor se ofrece a la IA como `<conector>_<herramienta>`; el riesgo sale de las listas `ReadOnlyTools`/`WriteTools`/`DangerousTools` (con comodín final) o de la anotación `readOnlyHint` del servidor, y si no, `DefaultRisk` (Dangerous). `DisabledTools` las oculta.

| Conector | Cómo | Estado inicial |
|---|---|---|
| `navegador` | Playwright MCP de Microsoft **fijado a 0.0.84**, con un Edge propio de Kroko (`%LOCALAPPDATA%\Kroko\navegador`). Leer/navegar = Read, clic/escribir = Write; código arbitrario, subir archivos, cookies y storage desactivados | Necesita que Pol pulse **Conectar** (descarga de npm) |
| `notion` | Notion MCP **remoto oficial** (`mcp.notion.com`, el local está obsoleto) con OAuth y registro automático | Necesita **Conectar** + "Permitir" |
| `google_calendar` | MCP oficial de Google (developer preview) con OAuth | Desactivado hasta tener cliente OAuth |

Los permisos OAuth se guardan cifrados con DPAPI en `%LOCALAPPDATA%\Kroko\conectores`. Ventana **"Conectores"** (clic derecho en Kroko) con estado y botón Conectar/Desconectar.

#### Cómo conecta Pol sus cuentas

> Lo más fácil: clic derecho en Kroko → **Configurar Kroko** (asistente con todos estos pasos; las claves se guardan cifradas). Lo de abajo es la vía manual.

1. **Gmail:** activar la verificación en 2 pasos de Google → crear una contraseña de aplicación en https://myaccount.google.com/apppasswords → guardar:
   ```
   dotnet user-secrets set "Email:Address" "tu.correo@gmail.com" --project src/Kroko.Brain
   dotnet user-secrets set "Email:AppPassword" "abcd efgh ijkl mnop" --project src/Kroko.Brain
   ```
2. **Navegador y Notion:** clic derecho en Kroko → Conectores → **Conectar** (Notion abre el navegador para dar "Permitir" y elegir qué páginas compartir).
3. **Google (Tasks, Calendar, Drive):** proyecto en Google Cloud → activar Google Tasks API, Google Calendar API y Google Drive API → pantalla de consentimiento (Externo, Pol como usuario de prueba) → ID de cliente OAuth tipo "App de escritorio" → pegar ID y secreto en Configurar Kroko → Tus apps → "Conectar mi cuenta de Google". En un servidor hace falta un cliente "Aplicación web" con la redirección `https://<dominio>/api/google/oauth/callback`.
4. **Trello:** https://trello.com/power-ups/admin → crear Power-Up "Kroko" → clave de API → "Generar token" en el asistente.
5. **Todoist:** clic derecho → Conectores → Conectar (OAuth oficial).

### Control visual (Fase 4)
`ver_pantalla()`, `leer_ventana_activa()` (UI Automation con FlaUI), `clic(elemento|coordenadas)`, `escribir(texto)`, `teclas(atajo)`. Siempre verificar con una nueva captura que la acción funcionó.

### Remoto (Fase 5)
Bot de Telegram: me avisa al móvil y puedo darle órdenes desde ahí (mismas reglas de confirmación).

---

## 10. Ventana de Ajustes (diseño premium)

Ventana WPF moderna (estilo Windows 11: Mica, esquinas redondeadas, navegación lateral) con secciones:
**General** (inicio con Windows, tamaño de la burbuja, posición, sonidos) · **Voz** (atajo, modo, voz TTS, micrófono con medidor) · **Cerebro** (URL del Brain, proveedor, estado de conexión) · **Personalidad** (nivel de humor, distracciones on/off, horario de sueño) · **Herramientas** (activar/desactivar cada una, lista blanca de scripts, alias) · **Apps conectadas** (MCP) · **Registro** (auditoría) · **Galería de expresiones** (previsualizar todas las caras — útil para probar el motor).

---

## 11. Bandeja del sistema

Icono de Kroko con menú: Hablar · Mostrar/Ocultar · Pausar · Ajustes · Salir.

---

## 12. Rendimiento y calidad

- Animaciones a 60 fps sin tirones; usar `RenderOptions.BitmapScalingMode` y caché de visuales donde ayude.
- En reposo: **< 2 % CPU** y **< 150 MB RAM**.
- Arranque en < 2 s; aparece con animación de `waking`.
- Reconexión automática y silenciosa al Brain; si no hay Brain, Kroko muestra `sad` y lo explica al tocarlo.
- Errores nunca muestran excepciones crudas al usuario.

---

## 13. Instalación y distribución

- Instalador y autoactualización con **Velopack**.
- Opción "Iniciar con Windows".
- El Brain local se lanza junto con la App mientras no esté en servidor (proceso hijo o servicio de Windows).

---

## 14. Configuración

- `Kroko.App/appsettings.json`: URL del Brain, token, atajos, tamaño, posición.
- `Kroko.Brain/appsettings.json`: proveedor activo, nombres de modelos, límites, servidores MCP.
- Secretos (API keys de Gemini, DeepSeek, Notion, Telegram): `dotnet user-secrets` en desarrollo.

---

## 15. Fases y criterios de aceptación

### Fase 0 — Esqueleto
Solución, 3 proyectos, DI, Serilog, `dotnet build` sin errores.
✅ La App abre una burbuja vacía; el Brain responde en `/health`.

### Fase 1A — Kroko vivo (solo diseño)
Ventana nativa completa (sección 4), motor de expresiones (sección 5) con **todas** las expresiones del catálogo, capas vivas, sonidos, galería de expresiones en una ventana de prueba.
✅ Puedo ver cada expresión en la galería, las transiciones son suaves, los ojos siguen el mouse, parpadea, respira, se duerme y despierta, se marea al arrastrarlo. No roba el foco.

### Fase 1B — Conexión y cerebro
WebSocket + protocolo (sección 3), `GeminiProvider` y `DeepSeekProvider`, bucle del agente con entrada de **texto** (doble clic → panel → escribo).
✅ Le escribo "hola" y responde con texto y expresión adecuada.

### Fase 1C — Voz
Grabación con atajo, medidor en el anillo, envío de audio, TTS con boca sincronizada, interrupción.
✅ Pulso Ctrl+Espacio, le hablo, me responde hablando.

### Fase 1D — Manos
Herramientas de Sistema y Archivos, niveles de riesgo, tarjeta de confirmación, interruptor de emergencia, auditoría.
✅ "Abre Excel", "¿cómo va mi batería?", "ordena mi escritorio" (pide confirmación), "apaga la PC en 10 minutos" (pide confirmación) y "cancela el apagado" funcionan.

### Fase 2 — Productividad
Recordatorios, temporizadores, pomodoro, hábitos, tareas, modo concentración, resumen diario, ventana de Ajustes completa, bandeja, inicio con Windows, instalador.

### Fase 3 — MCP
Cliente MCP + Notion, Google Calendar, Gmail.
✅ "Abre mi Notion y créame una página llamada Pendientes del lunes con estas tareas…" funciona.

### Fase 4 — Control visual
Captura + UI Automation + clic/teclado con verificación.

### Fase 5 — Remoto y servidor
Telegram, autenticación por token, despliegue del Brain en servidor (Docker), la App solo cambia la URL.

---

## 16. Al terminar cada fase

1. Resumen en español de lo que se hizo.
2. Pasos exactos para probarlo.
3. Problemas conocidos o pendientes.
4. Actualiza la sección **"Estado del proyecto"** al final de este archivo.

---

## Estado del proyecto

- [x] Fase 0 — Esqueleto (06/11/2026): `Kroko.slnx` con App (WPF), Brain (ASP.NET, `/health` en :5080) y Shared; DI + Serilog (logs en `%LOCALAPPDATA%\Kroko\logs`); burbuja estilo Troko con ventana nativa (no roba foco, fuera de Alt+Tab, PerMonitorV2), arrastrable, clic derecho → Salir. Ojos aún estáticos.
- [x] Fase 1A — Kroko vivo (06/11/2026): motor paramétrico en `Face/` (FaceAnimator + FaceRenderer + FaceView), 31 expresiones + 2 internas (`yawn`, `offline`) en `Face/expressions.json` con recarga en caliente (copia editable en `%LOCALAPPDATA%\Kroko\expressions.json`); parpadeo, mirada al cursor, respiración, hover, inclinación al arrastrar; sueño/despertar, distracciones (mosca, pelota, bostezo, reloj), clics → feliz/risa/enfado, arrastre rápido → mareo; batería baja, descarga terminada, sin internet; posición por monitor (`placement.json`); se esconde en pantalla completa; sonidos sintetizados (o `assets/sounds/*.wav`); galería (clic derecho o doble clic). Herramienta: `Kroko.App.exe --snapshot <carpeta> [segundos…]` dibuja todas las caras en PNG (ver `docs/expresiones.png`). Pendiente de medir: CPU con la burbuja visible (oculta: 0,02 %, RAM 122 MB).
- [x] Fase 1B — Conexión y cerebro (06/11/2026): probado con Gemini real ("hola" → respuesta + `happy`). Protocolo completo en `Kroko.Shared/Protocol` (12 tipos, JSON con `type`). Brain: `GeminiProvider` (`generateContent`, modelo `gemini-3.6-flash`, reenvía thought signatures), `DeepSeekProvider` (`deepseek-flash`, thinking desactivado, reenvía `reasoning_content`), `FallbackLlmProvider` según `Llm:Order`; bucle del agente (máx. 15 pasos, herramientas vía `IToolGateway`, vacío hasta la 1D); la IA elige la expresión con una etiqueta `[[clave]]`; personalidad en `prompts/system.md` (se relee en cada orden); historial en memoria por sesión (SQLite pendiente); `/ws` con token `X-Kroko-Token` (sin token: solo localhost); `/health/llm`. App: `BrainConnection` (reconexión 1→30 s, ping 15 s), `BrainLauncher` lanza el Brain como hijo (`--parent-pid`, se cierra con la App), panel de respuesta (doble clic) con efecto máquina de escribir y boca sincronizada, clic sin cerebro → explica; cara `offline` si el Brain cae > 6 s. Pruebas: `tests/Kroko.Tests` (26, incluye punta a punta por WebSocket). Medido: App visible en reposo 0,01 % CPU / 134 MB; Brain 73 MB. Panel sin Mica (fallback sólido; pendiente).
- [~] Fase 1C — Voz (07/11/2026): construida; **falta que Pol la pruebe con micrófono**. App: `Audio/VoiceIO.cs` (grabación 16 kHz con NAudio 3 `WaveIn`, medidor → anillo, reproducción con amplitud → boca/antena, voz de Windows de respaldo con System.Speech), `VoiceController` (atajo global `Voice:Hotkey`, modo `Tap` con fin por silencio o `PushToTalk`, máx. 30 s, sin micrófono → aviso, el atajo o Esc interrumpen). Brain: `Tts/GeminiSpeech.cs` (transcripción con `generateContent` + audio en línea; voz con `gemini-3.8-flash-tts` vía API Interactions, WAV 24 kHz, voz `Puck` y estilo peruano configurables en `Voice`), `user.audio` → `transcript` → agente → `reply` con audio. Probado de punta a punta con Gemini real usando una voz sintética (transcribió y respondió con audio).
- [x] Fase 1D — Manos (06/11/2026): catálogo único `Kroko.Shared/Tools/ToolCatalog.cs` (27 herramientas de la sección 9; riesgo según argumentos y texto de confirmación). Brain: `AppToolGateway` (Dangerous → `confirm.request`, 60 s sin respuesta = cancelado; las capturas llegan a Gemini como imagen). App: `Tools/` (una clase por herramienta; programas vía alias/App Paths/Get-StartApps/.lnk; volumen con NAudio; brillo con WMI; apagado con shutdown.exe y mínimo 10 s; Papelera con SHFileOperation, nunca en unidades sin Papelera; ordenar carpeta deja registro en `%LOCALAPPDATA%\Kroko\ordenes`), `PathGuard` (zonas protegidas), `ToolExecutor` como segunda barrera (recalcula el riesgo y exige la aprobación de la tarjeta, de un solo uso), tarjeta Confirmar en el panel (Enter/Esc, cara `alert`), Ctrl+Alt+K (`EmergencyHotkey`, cara `paused`), auditoría JSONL en `%LOCALAPPDATA%\Kroko\audit` + ventana "Registro de acciones". Panel y burbuja no aparecen con pantalla completa (ni al arrancar). Pruebas: 50 (flujo de confirmación de punta a punta y seguridad del ejecutor). Probado con Gemini real: estado del equipo y listar descargas. Pendiente que Pol pruebe en vivo: abrir Excel, ordenar escritorio, apagar/cancelar (necesitan su confirmación). Herramienta de desarrollo: `Kroko.App.exe --say "texto"`.
- [x] Extra — Capacidad de mejorarse (06/11/2026): SQLite en el Brain (`Memory/`, `%LOCALAPPDATA%\Kroko\kroko.db`, con WAL y versión de esquema): recuerdos, rutinas, buzón de mejoras e **historial persistente** (la conversación sobrevive a reinicios; la App guarda un id fijo en `session-id.txt`; al pasar de `HistoryMaxMessages` lo antiguo se resume con la IA en segundo plano y se incluye en el prompt). `CompositeToolGateway` = herramientas del Brain + de la App. Ventana "Lo que sabe de ti" (ver `docs/memoria.png`) con olvidar recuerdos, borrar rutinas, marcar mejoras como hechas y "Olvidar la conversación". Pruebas: 60 (las del Brain usan una base de datos temporal, nunca la real). Probado con Gemini real: recordó un dato tras reiniciar, guardó una rutina y anotó "Gmail" en el buzón.
- [~] Extra — Modo Jarvis (07/11/2026): búsqueda en internet y lectura de páginas (probado con Gemini real: clima de Urubamba con fuente), Gmail completo por IMAP/SMTP (pendiente: contraseña de aplicación de Pol para probarlo en vivo), cliente MCP genérico con OAuth y consentimiento previo, conectores navegador (Playwright 0.0.84) y Notion remoto listos para "Conectar" (pendiente: que Pol los conecte; la prueba en vivo del navegador necesita su permiso porque descarga de npm), Google Calendar preparado pero apagado. `confirmar_accion` para pasos irreversibles en webs. Arreglos: `type` del protocolo en cualquier posición. Pruebas: 87.
- [x] Fase 2 — Productividad (07/11/2026): Brain (`Productivity/`, esquema SQLite v2): recordatorios con repetición (nunca/diario/semanal/laborables), tareas, hábitos con racha, `resumen_del_dia` y resumen automático a las `Productivity:DailySummaryHour`; `ReminderScheduler` cada 15 s, solo si hay a quién avisar (los pendientes se entregan al reconectar); mensaje `notify` con voz. App: `temporizador`, `pomodoro` (25/5, descanso largo cada 4; cara `focus`), `modo_concentracion` (silencia las notificaciones emergentes de Windows y las restaura al salir), tarjeta con anillo de cuenta regresiva en el panel, avisos en cola si Kroko está ocupado o en pantalla completa; bandeja del sistema (H.NotifyIcon, icono = la cara de Kroko); ventana de **Ajustes** (General, Voz, Cerebro, Personalidad, Herramientas con desactivar/alias/lista blanca, Más) que guarda en `%LOCALAPPDATA%\Kroko\settings.json`; inicio con Windows (HKCU\Run); `ejecutar_script` solo de la lista blanca y con confirmación; Velopack (`Program.cs`, `UpdateService` con `Kroko:UpdatesUrl`) y `scripts/empaquetar.ps1` (no ejecutado: necesita `dotnet tool install -g vpk`). Probado con Gemini real: recordatorio a 1 minuto entregado solo, con voz.
- [~] Fase 3 — MCP (07/11/2026): cliente MCP genérico hecho (ver Modo Jarvis). **Falta:** que Pol conecte Notion (botón Conectar) y pruebe el criterio "créame una página…"; Google Calendar/Drive cuando haga el proyecto en Google Cloud; Gmail ya funciona por IMAP en cuanto ponga la contraseña de aplicación.
- [x] Fase 4 — Control visual (07/11/2026): FlaUI.UIA3 5.0. `leer_ventana_activa` (elementos numerados de la última ventana de Pol que no es Kroko, `ForegroundTracker`), `clic` (por número, texto o coordenadas; devuelve la ventana después para verificar; botones irreversibles → Dangerous y la herramienta se niega si la IA no lo marcó), `escribir` (nunca en campos de contraseña ni datos que parezcan tarjeta/contraseña), `teclas` (Alt+F4, Ctrl+W, Supr… → Dangerous). **Pendiente de probar en vivo por Pol.**
- [~] Fase 5 — Remoto y servidor (07/11/2026): `deploy/` (Dockerfile del Brain, docker-compose con Caddy y HTTPS automático, `.env.example`), guía `docs/DESPLIEGUE.md`; el Brain **no arranca expuesto sin token** (`ServerSafety`); datos en `/data` (`XDG_DATA_HOME`); OAuth en modo servidor (`Mcp:PublicBaseUrl` + `/api/conectores/oauth/callback`, la App abre el enlace); Telegram (`Telegram/TelegramBot.cs`: texto y notas de voz, solo `AllowedChatIds`, confirmaciones con botones, herramientas de la PC vía la App conectada con `approvedRemotely`, avisos al móvil). Probado: publicación para Linux y modo servidor con/sin token. Imagen construida y probada en Docker (07/11/2026: API con token, 401 sin token, perfil y claves persisten tras reiniciar, `secretos.dat` con permisos 600; arreglado que escuchaba en localhost:5080 dentro del contenedor). **Falta:** desplegar en un VPS real (recomendado: Oracle Cloud Always Free + DuckDNS) y crear el bot de Telegram.
- [x] Extra — Kroko se adapta a su dueño (07/11/2026): **asistente "Configurar Kroko"** (`Views/OnboardingWindow`, 7 pasos: bienvenida, sobre ti, cerebro IA, Gmail, Telegram, superpoderes, listo; ver `docs/bienvenida-*.png`) que se abre solo la primera vez (`OnboardingLauncher`, espera si hay pantalla completa) y desde clic derecho → Configurar Kroko o Ajustes → Más. Brain: perfil del dueño (`Owner/OwnerProfile.cs`: nombre, cómo llamarlo, ciudad, país, zona horaria IANA, acento, sobre mí; en la tabla `kv`), `OwnerTimeProvider` (la hora de Kroko es la del dueño, también en un servidor en UTC), `prompts/system.md` con marcadores `{nombre}`, `{ubicacion}`, `{acento}` (el estilo de la voz usa `{acento}`), herramienta `perfil_actualizar` ("llámame Rodo"); si no sabe quién es, se presenta y pregunta. Ya no hay "Pol" fijo en el código (la herramienta pasó a llamarse `confirmar_accion`). Claves y cuentas del asistente en `secretos.dat` (`Config/SecretStore.cs`, DPAPI; en Docker archivo 600 en `/data`), fuente de configuración que se recarga sin reiniciar; solo se aceptan las claves de IA, Tavily, Gmail y el token de Telegram. API `/api/perfil`, `/api/configuracion` (+ `/secretos`, `/probar/{gemini|deepseek|tavily|correo|telegram}`), `/api/telegram/vincular|estado|vinculo`. Telegram: el bot se reinicia solo al cambiar el token y se vincula con un código de 6 cifras de un solo uso (10 min) o el enlace `t.me/<bot>?start=<código>`. Tavily (app.tavily.com) como segundo buscador y lector de páginas difíciles. Pruebas: 97.
- [x] Extra — Voz manos libres y rediseño (07/11/2026): **«Oye Kroko» / «Hola Kroko»** con Vosk local (`Audio/WakeWord.cs`: `VoskModelManager` descarga y verifica el modelo, `WakeWordDetector` solo decide al cerrar la frase, `WakeWordService` en un hilo aparte; medido con voz sintética: 43/45 aciertos y 0 falsas alarmas en 153 frases). Si dices la orden de corrido ("oye kroko, abre Excel") la manda entera. **Conversación continua** (`VoiceController`): tras responder vuelve a escuchar `Voice:FollowUpSeconds` (8 s); termina al callarte o con `Voice:EndPhrases`; el atajo, Esc u «Oye Kroko» lo interrumpen. Un solo micrófono compartido (`Audio/MicrophoneStream.cs`, se recupera si lo desconectas). **Subtítulos en vivo** de lo que dices (Vosk, local). Panel rediseñado: vidrio oscuro con reflejo, resplandor lima y onda de voz al escuchar, indicador "En conversación", **tarjetas Lista** (tareas/hábitos con check, desde el Brain) y **Estado del equipo** (barras), mensaje nuevo `card` (`Kroko.Shared/Protocol/Cards.cs`). **Ajustes rediseñado** estilo Windows 11 (iconos, filas con interruptores, sección Voz y conversación con descarga del modelo y medidor del micrófono; los interruptores de voz se aplican al instante). Bandeja: "Escuchar «Oye Kroko»". Capturas: `docs/panel.png`, `docs/ajustes-*.png`. Pruebas: 102 (Brain) + 44 (App, incluye «Oye Kroko» con el modelo real). Mica real no es posible en una ventana transparente: el vidrio se dibuja. **Pendiente que Pol lo pruebe con micrófono** y medir la CPU escuchando.
- [x] Extra — Apps, automatizaciones y Kroko discreto (07/11/2026): **automatizaciones con IA** (`Kroko.Brain/Automation/`, esquema SQLite v3): se crean hablando ("de lunes a viernes a las 8 dime mis pendientes y notas"), herramientas `automatizacion_crear|automatizaciones_listar|automatizacion_cambiar|automatizacion_probar`; `AutomationScheduler` cada 30 s (solo dentro de las 3 h siguientes a su hora, una vez al día); corren en una conversación aparte que se borra; resultado por Telegram y/o la App; lo peligroso pide permiso con botones de Telegram (o la tarjeta en la PC). Probado con Gemini real: creó y ejecutó "noticias de Perú a las 7". **Conectores de tareas:** Todoist (MCP oficial `ai.todoist.net/mcp`, botón Conectar), Trello (`Apps/Trello.cs`, clave + token, 6 herramientas; archivar pide permiso), **cuenta de Google** (`Apps/Google.cs`, OAuth con PKCE y refresh token cifrado: Google Tasks, Calendar —crear eventos pide permiso— y Drive solo lectura; API `/api/google/*`). El prompt junta pendientes de todas las fuentes (Kroko, Todoist, Trello, Google Tasks, notas de Notion). Asistente: paso **"Tus apps"** (Notion/Todoist, Trello, Google). **Kroko discreto:** tras `HideAfterSleepMinutes` (5) dormido se desvanece; vuelve con «Oye Kroko», el atajo, la bandeja o cuando tiene un aviso (sigue escuchando escondido). Pruebas: 108 (Brain) + 44 (App).
- [x] Extra — Asistente completo y publicación (07/11/2026): **voz en tiempo real** con Gemini Live (`gemini-3.8-live`, `Agent/LiveSession.cs` en el cerebro + `Services/LiveConversation.cs` y `Audio/LivePlayer.cs` en la App; mensajes `live.start|audio|stop|event`; las herramientas pasan por el mismo `IToolGateway` con confirmaciones y auditoría; lo dicho se guarda en el historial; sin cancelación de eco, mientras Kroko habla no se envía el micrófono y se le interrumpe con «Oye Kroko» o el atajo; si falla, modo clásico; probado de punta a punta con Gemini real). **Avisos proactivos** (`Productivity/Proactive.cs`, sección `Proactive`: reunión 10 min antes, correos importantes de Gmail, tareas vencidas de Kroko/Google Tasks/Trello una vez al día, horario de silencio). **Contactos de Google** (`google_contactos_buscar`, People API). **Música** (`musica`: teclas multimedia y búsqueda en Spotify/YouTube Music). **Búsqueda en documentos** por significado (`Services/Documents.cs`: índice en `%LOCALAPPDATA%\Kroko\documentos.db`, Word/PDF/texto; vectores `gemini-embedding-2` de 768 dims calculados por el cerebro en `/api/vectores`; herramientas `buscar_en_documentos` y `leer_documento`; el texto de los documentos va a Gemini). **«Sí, hazlo» por voz** solo para acciones medianas (`Kroko.Shared/Tools/VoiceConfirm.cs`). **Mi día** (`/api/midia` + ventana `MyDayWindow`, clima de Open-Meteo; menú, bandeja y herramienta `mostrar_mi_dia`). **Publicación:** repo público `rodolfo-riveros/kroko`; la App ya no lleva el cerebro (solo servidor; el local solo en desarrollo), se instala como `KrokoApp` (separada de los datos) y se actualiza sola desde GitHub Releases reiniciándose cuando duerme (`UpdateService`); flujos `.github/workflows` (pruebas, `release-app.yml` con `vpk` 1.2.161 al crear un tag `v*`, `brain-image.yml` a `ghcr.io` amd64+arm64, Gitleaks); `deploy/docker-compose.yml` con la imagen de GHCR + Watchtower (fork `nickfedor/watchtower`); bloqueo local de commits con claves (`.githooks/pre-commit`, activado); si la App no encuentra el cerebro, el asistente pide dirección y token. Quitado: conector MCP de Google Calendar (lo reemplaza la cuenta de Google). Pruebas: 120 (cerebro) + 47 (App).

- [x] Extra — Voz o solo chat (08/11/2026): Kroko responde **hablando también a lo que escribes** (voz de Gemini; `Voice:SpeakTypedReplies` ahora encendido por defecto). Modo **solo chat** con «cállate / mándamelo por chat» y vuelta con «háblame otra vez» (herramienta de la App `modo_respuesta`, Write, se guarda en `settings.json`) o en Ajustes → Voz → Respuestas → "Responder con voz". En solo chat no suena nada (ni avisos), no se usa Gemini Live y el cerebro no gasta voz: la App manda `speak` en `user.text`/`user.audio` (si falta, comportamiento clásico). Despliegue en VPS propio con puertos ocupados: `deploy/docker-compose.vps.yml` + `docs/VPS-PROPIO.md`. Pruebas: 124 (cerebro) + 54 (App).

- [x] Extra — Kroko elige la app y abre su configuración (08/11/2026): `Agent/AppRouting.cs` arma en cada orden qué apps están vinculadas de verdad (Google, Todoist, Trello, Notion) y dónde va cada cosa: eventos → Google Calendar, tareas → Todoist > Google Tasks > Trello (Trello para proyectos si hay otra), notas → Notion; sin nada vinculado, a su memoria (`tarea_crear`, `recordatorio_crear`, `recordar` con "Nota:"); los avisos siempre con `recordatorio_crear`; respeta la preferencia del dueño y la guarda si lo corrige. Herramienta de la App `abrir_ventana` (Read): configuración con sección, asistente, conectores, memoria, registro, galería, mi día ("abre tu configuración"). Ajustes: la pestaña se elige con `SelectedSection`. Arreglado: cerebro y App desfasados en desarrollo (compilar la App compila el cerebro en Debug), voz de Windows al escribir, icono de bandeja. README con enlace de descarga. Probado con Gemini real (5 órdenes sin apps vinculadas). Pruebas: 130 (cerebro) + 56 (App).

- [x] Extra — Micrófono y Notion como agenda (08/11/2026): el micrófono se vigila cada 2 s (`MicrophoneStream.DevicesChanged`): si conectas o quitas uno con Kroko abierto, se reabre y «Oye Kroko» empieza a escuchar sin reiniciar (antes solo se miraba al arrancar y no escuchaba nunca). «Predeterminado de Windows» usa de verdad el micrófono por defecto (WAVE_MAPPER, -1) en vez del primero de la lista. Los audífonos Bluetooth tardan 1–2 s en pasar a "manos libres" (medido). **Notion = notas y agenda** (decidido por Pol): todo va a la base «Kroko · Notas y agenda» (la crea una vez: Nombre, Tipo Nota/Idea/Agenda/Reunión/Lista, Fecha, Etiquetas, vistas Calendario y Por tipo; guarda su enlace en la memoria), una página por nota; los eventos con fecha van a Notion **y** a Google Calendar; las tareas no van a Notion. `notion-create-pages/database/view` pasan a `Write` (sin tarjeta); editar, mover, duplicar, comentar y borrar siguen pidiendo permiso. Pruebas: 141 (cerebro) + 57 (App, incluye el micrófono real si hay uno).

- [x] Extra — Sin preguntar lo que Pol pidió (08/11/2026): `Kroko:AutoApproveTools` (por defecto apagar, reiniciar, cerrar sesión, ordenar carpeta, mover y copiar archivos) se aprueban solas en la App (`PanelViewModel.ShowConfirm` → mismo camino que «Sí, hazlo», el ejecutor sigue verificando) si el dueño dio una orden en los últimos `AutoApproveMinutes` (3); automatizaciones y avisos siguen con tarjeta. Pruebas: 141 (cerebro) + 69 (App). Publicada la **v1.0.1**.

- [x] Extra — Dirección del cerebro desde Ajustes (08/11/2026, v1.0.2): `Services/BrainAddress.cs` acepta el dominio como se escriba ("kroko.midominio.com", con barra, `/ws`, `wss://`, "localhost:5080") y lo deja canónico (https en internet, http en esta PC); se aplica al cargar las opciones (`PostConfigure`), en Guardar de Ajustes → Cerebro y en el asistente, así una dirección mal escrita nunca rompe el arranque. **Comprobar** prueba lo escrito sin guardarlo y distingue "no encuentro el cerebro", "falta el token" y "token incorrecto" (`/health/llm` abierto + `/api/perfil` protegido). El token se muestra oculto. Todas las claves y cuentas (Gemini, DeepSeek, Tavily, Gmail, Telegram, Trello, Google) se ponen en Configurar Kroko y se guardan cifradas; en el código solo quedan las direcciones públicas de cada servicio (configurables). Pruebas: 141 (cerebro) + 84 (App).

- [x] Extra — Un cerebro por persona en el mismo servidor (08/11/2026, decidido por Pol: él y 2 amigos, sin crear cuentas por ahora): `deploy/persona.sh` (nueva, lista, datos, logs, parar/iniciar, actualizar, borrar) + `deploy/docker-compose.persona.yml` (proyecto Docker `kroko-<nombre>` con su volumen, puerto 127.0.0.1:8091+) + `docker-compose.watchtower.yml` (uno para todos). El token de cada persona es su cuenta; cada uno pone su propia clave de Gemini en Configurar Kroko. `deploy/personas/` y `*.env` fuera de git y del hook. Guía en `docs/VPS-PROPIO.md`. Probado con Docker: dos personas aisladas (perfil de una invisible para la otra, token cruzado → 401, datos sobreviven al reinicio, borrar con y sin datos).

- [x] Extra — Archivos y todo por Telegram (08/11/2026): herramienta de la App `enviar_archivo` (Write): lee el archivo (hasta 45 MB, nunca carpetas) y lo manda dentro del resultado; `AppToolGateway.Split` lo saca antes de dárselo a la IA (solo ve "enviado") y `ITelegramDelivery` (el bot) lo envía como documento al chat desde el que se pidió o, si se pidió en la PC, al del dueño (nunca a un chat no vinculado). `FileSendGuard`: nunca `.env`, llaves (`id_rsa`, `.pem`, `.pfx`, `.kdbx`…), carpetas de Kroko, `.ssh`, credenciales de Windows ni perfiles de navegadores. El registro de acciones no guarda el contenido. `MaxMessageBytes` 16 → 64 MB. En Telegram ahora también llegan las tarjetas (listas y estado del equipo, `TelegramFormat.Card`) y las capturas de pantalla pedidas desde allí. Pruebas: 144 (cerebro) + 97 (App).

- [x] Arreglo — Notion no guardaba nada y salía «[[happy]]» (08/11/2026): la IA, en voz en tiempo real, intentaba crear la base paso a paso y se perdía (dejó solo una página «Kroko» vacía). Ahora `guardar_en_notion` (`Apps/NotionNotes.cs`, Write) lo hace por código en un paso: busca o crea UNA vez la base «Kroko · Notas y agenda» (`notion-create-database` con DDL + vistas Calendario y Por tipo), guarda su `collection://` en la tabla `kv` y agrega la nota (`notion-create-pages` con Tipo, Fecha con la zona del dueño y Etiquetas; si Notion rechaza las etiquetas, reintenta sin ellas; si la base se borró, la recrea). `McpHub.CallAsync` permite llamar a un conector desde código. Etiquetas `[[…]]`: la voz en tiempo real ya no recibe la orden de ponerlas (`SystemPrompt.Build(expressionTags: false)`, antes se contradecía), lo que dice se limpia (y la etiqueta se vuelve cara) y el panel quita cualquier etiqueta (`Reactions.StripTags`). Nota de diagnóstico: Claude corre como paquete de Windows y ve una copia virtualizada de `%LOCALAPPDATA%Kroko` (registros, base de datos): no sirve para revisar los datos reales de Kroko. Pruebas: 156 (cerebro) + 97 (App).

- [x] Arreglo — Siempre «Kroko» y el chat sin respuestas viejas (08/11/2026, **v1.0.3** con todo lo anterior: archivos por Telegram, `guardar_en_notion`, sin `[[…]]`): `Kroko.Shared/KrokoName.Fix` corrige «croco», «cro co», «kroco», «croko»… en el dictado (Gemini), la voz en tiempo real (frase completa), los subtítulos de Vosk, lo escrito y las respuestas (`ExpressionTag.Extract`), así el historial queda bien; el prompt avisa que «croco» es su nombre. En voz en tiempo real, al empezar una frase nueva se borra la respuesta anterior del panel (`PanelViewModel.StartLiveUserTurn`). Pruebas: 165 (cerebro) + 97 (App).

### Qué falta para dar todo por terminado (07/11/2026)

1. **Pruebas en vivo de Pol:** asistente "Configurar Kroko" (se abre solo al arrancar si no hay perfil), voz con micrófono (1C, «Oye Kroko» y conversación), acciones con confirmación (1D), control visual (4), Notion y navegador (Conectar), Gmail (contraseña de aplicación).
2. **Servidor:** VPS gratis (Oracle Always Free) + DuckDNS + `docker compose up -d` en `deploy/` (guía `docs/DESPLIEGUE.md`); hacer público el paquete `kroko-brain` en GitHub; crear el bot de Telegram.
3. **Primera publicación:** subir a GitHub (`rodolfo-riveros/kroko`, público) y crear el tag `v1.0.0`: GitHub Actions publica el instalador y la imagen del cerebro.
4. **Cuentas de apps:** Google (proyecto en Google Cloud + cliente OAuth), Trello (clave + token) y Todoist (Conectar) desde Configurar Kroko → Tus apps.
5. **Pulido pendiente:** probar «Oye Kroko» y la conversación con micrófono real (y ajustar `Voice:WakeWord:Decoys` si se activa por error); medir CPU con la escucha encendida.
