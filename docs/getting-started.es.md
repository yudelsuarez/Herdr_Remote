<p align="center"><a href="getting-started.md">English</a> · <b>Español</b></p>

# Primeros pasos

La configuración toma unos minutos y se hace una sola vez.

## 1. Prepara tu Mac

1. Instala [herdr](https://herdr.dev) y comprueba que funciona. La app necesita `hc`, la herramienta de línea de comandos de
   herdr, en el Mac.
2. Activa la **Sesión remota**: Ajustes del Sistema → General → Compartir → Sesión remota. En "Permitir
   acceso a", elige solo tu propio usuario.
3. Busca la dirección del Mac: en esa misma pantalla, macOS muestra una línea como `ssh tunombre@192.168.1.20`. Lo que va
   después de la `@` es la dirección, y lo que va antes es tu usuario.

## 2. Conecta el teléfono

1. Abre Remote Shell y ve a **Configuración → Mac**.
2. Escribe la **Dirección del Mac** y tu **Usuario**. Deja el **Puerto** en 22, salvo que lo hayas cambiado en el Mac.
3. Escribe la **Contraseña del Mac**. Se usa una sola vez, para agregar la llave de este teléfono al Mac, y nunca se guarda.
4. Toca **Guardar y conectar**.
5. La app te muestra la huella del Mac y pregunta **¿Confiar en este Mac?** Para estar seguro, toca **Detalles** para ver la huella completa y
   el comando `ssh-keygen -lf ...` que debes ejecutar en el Mac, y compáralas. Toca **Confiar**.

Listo: tus spaces y tus agentes aparecen en la pestaña **Herdr**. Desde ahora el teléfono se conecta con su propia llave.

**¿Prefieres no escribir la contraseña?** En la pantalla de conexión, copia **La llave de este teléfono** y agrega tú mismo esa
línea a `~/.ssh/authorized_keys` en el Mac.

## 3. Cómo usarla

- **Pestaña Herdr:** tus spaces, sus tabs y paneles, y cada agente con su estado. Toca un panel para abrir su terminal.
- **En una terminal:** la barra de teclas te da Esc, Tab, Ctrl y flechas; el micrófono dicta; mantén pulsado sobre el texto y
  elige **Leer desde aquí** para escucharlo.
- **Pestaña Lector:** pega cualquier texto y escúchalo.
- **Configuración:** tema, idioma, teclas y atajos, voz de lectura y dictado.

## Si algo falla

| Lo que ves | Qué hacer |
|---|---|
| La app no llega al Mac | Comprueba que el teléfono y el Mac estén en la misma red (o que tu VPN esté activa) y que la Sesión remota esté activada. En iPhone, permite **Red local** para Remote Shell en los Ajustes del iPhone. |
| "Contraseña no aceptada" | Escríbela otra vez. El Mac debe permitir iniciar sesión por SSH con contraseña. |
| "No se encontró herdr en el Mac" | Instala herdr en el Mac. La app busca `hc` en tu PATH, en `~/.local/bin`, `/opt/homebrew/bin` y `/usr/local/bin`. |
| "herdr no está abierto" | Abre herdr en el Mac y toca **Intentar de nuevo**. |
| "La identidad de este Mac cambió" | Si no reinstalaste ni cambiaste el Mac, no continúes. Si lo hiciste, toca **Olvidar la llave del Mac** en los ajustes de conexión y vuelve a confiar en el Mac. |

Más sobre qué protege la app y cómo: [Seguridad y privacidad](security-and-privacy.es.md).
