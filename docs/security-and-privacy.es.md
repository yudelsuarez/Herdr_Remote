<p align="center"><a href="security-and-privacy.md">English</a> · <b>Español</b></p>

# Seguridad y privacidad

Remote Shell le da a tu teléfono acceso a las terminales de tu Mac, así que es lógico preguntarse cómo se protege ese acceso. Esta
página lo explica en palabras simples. También es la política de privacidad de la app.

## En resumen

- Tu teléfono habla **directo** con tu Mac por SSH. No hay ningún servidor de Remote Shell, ni cuentas, ni registro.
- La app **no tiene analítica, rastreo, publicidad ni reportes de errores** enviados a ningún lado.
- Lo que hay en tus terminales queda entre tu teléfono y tu Mac. La única excepción es un servicio de voz o de dictado en la
  nube que **tú** decidas activar, con **tu propia** clave.

## Cómo funciona la conexión

1. **El teléfono tiene su propia llave.** La primera vez, la app crea en el teléfono un par de llaves SSH Ed25519. La llave
   privada nunca sale del teléfono: se guarda en el almacenamiento seguro del sistema (el Llavero en iPhone, almacenamiento
   cifrado respaldado por el Android Keystore en Android) y queda fuera de las copias de seguridad y de los traspasos a un
   teléfono nuevo.
2. **Tú confirmas el Mac.** La primera vez que se conecta, la app te muestra la huella del Mac y te pide confiar en ella. Desde
   ese momento esa identidad queda fijada: si otra máquina responde en esa dirección, la app se niega a conectarse y te avisa.
   Solo acepta una identidad nueva si tú, a propósito, olvidas la anterior en los ajustes de conexión.
3. **La contraseña del Mac se usa una sola vez.** Para dejar entrar al teléfono escribes la contraseña de tu Mac una vez. La app
   la usa solo para agregar la llave pública del teléfono a la lista de llaves permitidas de tu Mac, y solo después de que
   confirmaste la huella del Mac. La contraseña nunca se guarda; la app borra su copia en cuanto termina de usarla. A partir de
   ahí, el teléfono entra con su llave.
4. **Un solo canal cifrado.** Todo (la lista de agentes, lo que muestran las terminales, lo que escribes, las imágenes que
   envías) viaja dentro de esa única conexión SSH hacia `hc`, la herramienta de línea de comandos de herdr, que corre con tu
   propio usuario en el Mac.

La app no abre ningún puerto en tu teléfono ni espera conexiones entrantes.

## Qué datos van a dónde

| Dato | A dónde va |
|---|---|
| Lo que muestran las terminales, el estado de los agentes, lo que escribes y tus comandos | Solo entre tu teléfono y tu Mac, cifrado por SSH. |
| Imágenes que adjuntas o marcas y envías a un agente | Se copian a una carpeta temporal en tu Mac para que el agente pueda leerlas. La app borra de esa carpeta los archivos de más de un día. |
| Dictado con la opción **del dispositivo** (la predeterminada) | Se procesa en el teléfono. En iPhone la app exige reconocimiento en el dispositivo; en Android usa el reconocedor del propio teléfono. Ningún audio sale del teléfono. |
| Dictado con una opción **en la nube** (OpenAI, ElevenLabs, Azure, Inworld) | Cuando terminas de dictar, la grabación se sube a ese proveedor con tu clave. Si la subida falla, la grabación se queda en el teléfono hasta que la reintentes o la descartes. |
| Lectura en voz alta con la voz **del dispositivo** | La dicen las voces del propio teléfono. No se envía nada. |
| Lectura en voz alta con una voz **en la nube** (OpenAI, ElevenLabs, Azure, Fish Audio, Inworld) | El texto que se lee se envía a ese proveedor, con tu clave. |
| Lectura en voz alta con **tu propio servidor** | El texto se envía solo a la dirección que escribiste. |
| Claves de los servicios de voz y dictado | Guardadas en el almacenamiento seguro del teléfono. Se envían solo a su propio servicio. |
| Ajustes (dirección y usuario del Mac, tema, idioma, teclas que configuras) | Guardados en el teléfono (en iPhone también forman parte de tus propias copias de seguridad). Nunca se envían a ningún servidor de Remote Shell. |

Cuando usas un proveedor en la nube, lo que le envías queda sujeto a su propia política de privacidad.

## Avisos

Los avisos como "el agente terminó" o "el agente te necesita" se crean **en el teléfono** a partir de lo que informa el Mac. No
pasan por los servicios de notificaciones de Apple ni de Google, así que llegan mientras la app está abierta o lleva poco en
segundo plano: la app mantiene su conexión hasta 15 minutos en segundo plano, y iOS puede cortarla antes.

## Permisos que pide la app

| Permiso | Para qué |
|---|---|
| Red local (iPhone) | Para llegar a tu Mac por tu Wi-Fi. |
| Micrófono, reconocimiento de voz | Solo mientras dictas. |
| Cámara | Solo cuando decides tomar una foto para enviarla a un agente. |
| Agregar a Fotos (iPhone) | Solo cuando tocas "Guardar en fotos" en una imagen. |
| Notificaciones | Para avisarte cuando un agente termina o te necesita. |
| Audio en segundo plano | Para que la lectura siga con la pantalla apagada o con otra app delante. |
| Excepción de batería (Android) | Para que la conexión, y con ella los avisos de los agentes, no se corte con la app en segundo plano. |

## Buenas prácticas de tu lado

- Deja la Sesión remota limitada a tu propio usuario en el Mac (Ajustes del Sistema, Compartir, Sesión remota,
  "Permitir acceso a").
- Si pierdes un teléfono, borra su línea de `~/.ssh/authorized_keys` en tu Mac. La línea de cada teléfono termina con un
  comentario que nombra la app, así que es fácil de encontrar.
- Conéctate a tu Mac desde la red de tu casa o por una VPN en vez de exponer SSH a internet.

## Reportar un problema

Si encuentras un problema de seguridad, repórtalo en privado como explica [SECURITY.md](../SECURITY.md).
