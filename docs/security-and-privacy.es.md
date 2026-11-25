<p align="center"><a href="security-and-privacy.md">English</a> · <b>Español</b></p>

# Seguridad y privacidad

Remote Shell le da a tu teléfono acceso a las terminales de tu máquina, así que es lógico preguntarse cómo se protege ese acceso. Esta
página lo explica en palabras simples. También es la política de privacidad de la app.

## En resumen

- Tu teléfono se conecta **directo** a tu máquina por SSH. No hay ningún servidor de Remote Shell, ni cuentas, ni registro.
- La app **no tiene analítica, rastreo, publicidad ni reportes de errores** enviados a ningún lado.
- Lo que hay en tus terminales queda entre tu teléfono y tu máquina. La única excepción es un servicio de voz o de dictado en la
  nube que **tú** decidas activar, con **tu propia** clave.

## Cómo funciona la conexión

1. **El teléfono tiene su propia llave.** La primera vez, la app crea en el teléfono un par de llaves SSH Ed25519. La llave
   privada nunca sale del teléfono: se guarda en el almacenamiento seguro del sistema (el Llavero en iPhone, almacenamiento
   cifrado respaldado por el Android Keystore en Android) y queda fuera de las copias de seguridad y de los traspasos a un
   teléfono nuevo.
2. **Verificas la identidad de la máquina.** La primera vez que se conecta, la app te muestra la huella de la host key y te pide
   confiar en la máquina. Desde entonces, esa identidad queda fijada: si la host key cambia, la app no se conecta y te avisa.
   Para aceptar una identidad nueva que ya verificaste, usa **Olvidar la host key** en **Configuración → Máquina** y confirma la huella nueva.
3. **La contraseña de la máquina se usa una sola vez.** Si usas una contraseña para la configuración, la app solo la usa para
   agregar la llave pública del teléfono a la lista de llaves permitidas de la máquina, después de que confirmes la huella.
   La contraseña nunca se guarda; la app borra su copia en cuanto termina de usarla. Después, el teléfono se conecta con su llave.
4. **Un solo canal cifrado.** Todo (la lista de agentes, lo que muestran las terminales, lo que escribes, las imágenes que
   envías) viaja dentro de esa única conexión SSH hacia `hc`, la herramienta de línea de comandos de herdr, que corre con tu
   propia cuenta en la máquina.

La app no abre ningún puerto en tu teléfono ni espera conexiones entrantes.

## Qué datos van a dónde

| Dato | A dónde va |
|---|---|
| Lo que muestran las terminales, el estado de los agentes, lo que escribes y tus comandos | Solo entre tu teléfono y tu máquina, cifrado por SSH. |
| Imágenes y otros archivos que adjuntas, incluidas las imágenes que marcas | Herdr los copia a la máquina del panel seleccionado para que el agente pueda leerlos. Herdr los conserva hasta que se elimina el registro de esa terminal. |
| Dictado con la opción **del dispositivo** (la predeterminada) | Se procesa en el teléfono. En iPhone la app exige reconocimiento en el dispositivo; en Android usa el reconocedor del propio teléfono. Ningún audio sale del teléfono. |
| Dictado con una opción **en la nube** (OpenAI, ElevenLabs, Azure, Inworld) | Cuando terminas de dictar, la grabación se sube a ese proveedor con tu clave. Si la subida falla, la grabación se queda en el teléfono hasta que la reintentes o la descartes. |
| Lectura en voz alta con la voz **del dispositivo** | La dicen las voces del propio teléfono. No se envía nada. |
| Lectura en voz alta con una voz **en la nube** (OpenAI, ElevenLabs, Azure, Fish Audio, Inworld) | El texto que se lee se envía a ese proveedor, con tu clave. |
| Lectura en voz alta con **tu propio servidor** | El texto se envía solo a la dirección que escribiste. |
| Claves de los servicios de voz y dictado | Guardadas en el almacenamiento seguro del teléfono. Se envían solo a su propio servicio. |
| Configuración (dirección de la máquina y usuario, tema, idioma, teclas que configuras) | Guardada en el teléfono (en iPhone también forma parte de tus propias copias de seguridad). Nunca se envía a ningún servidor de Remote Shell. |

Cuando usas un proveedor en la nube, lo que le envías queda sujeto a su propia política de privacidad.

## Avisos

Los avisos como "el agente terminó" o "el agente te necesita" se crean **en el teléfono** a partir de lo que informa la máquina. No
pasan por los servicios de notificaciones de Apple ni de Google, así que llegan mientras la app está abierta o lleva poco en
segundo plano: la app mantiene su conexión hasta 15 minutos en segundo plano, y iOS puede cortarla antes.

## Permisos que pide la app

| Permiso | Para qué |
|---|---|
| Red local (iPhone) | Para conectarte a tu máquina por Wi-Fi. |
| Micrófono, reconocimiento de voz | Solo mientras dictas. |
| Cámara | Solo cuando decides tomar una foto para enviarla a un agente. |
| Agregar a Fotos (iPhone) | Solo cuando tocas "Guardar en fotos" en una imagen. |
| Notificaciones | Para avisarte cuando un agente termina o te necesita. |
| Audio en segundo plano | Para que la lectura siga con la pantalla apagada o con otra app delante. |
| Excepción de batería (Android) | Para que la conexión, y con ella los avisos de los agentes, no se corte con la app en segundo plano. |

## Buenas prácticas de tu lado

- Limita el acceso por SSH a las cuentas que lo necesiten. En macOS, limita Sesión remota a tu cuenta.
- Si pierdes un teléfono, borra solo la línea de su llave pública del [archivo de llaves autorizadas para tu máquina y tu cuenta](getting-started.es.md#configura-la-llave-a-mano).
  Busca el comentario al final de la línea: empieza con `remoteshell@` seguido del nombre del teléfono.
- Conéctate a tu máquina desde la red de tu casa o por una VPN como [Tailscale](getting-started.es.md#úsala-fuera-de-casa)
  en vez de exponer SSH a internet. Nunca hace falta abrir un puerto en tu router.

## Reportar un problema

Si encuentras un problema de seguridad, repórtalo en privado como explica [SECURITY.md](../SECURITY.md).
