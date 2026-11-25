<p align="center"><a href="getting-started.md">English</a> · <b>Español</b></p>

# Primeros pasos

La configuración toma unos minutos y se hace una sola vez.

## 1. Prepara tu máquina

1. Instala y abre [herdr](https://herdr.dev) en tu máquina con macOS, Linux o Windows. La app necesita `hc`, la herramienta
   de línea de comandos de herdr.
2. **Activa SSH en la máquina (en un Mac: Sesión remota) y luego escribe su dirección y tu usuario.**
   - **macOS:** Ve a **Configuración del Sistema → General → Compartir**, haz clic en **Información** junto a **Sesión remota** y actívala.
     En **Permitir acceso a**, elige **Solo estos usuarios** y permite el acceso solo a tu cuenta. [Guía de Apple](https://support.apple.com/es-us/guide/mac-help/mchl26e04309/mac)
   - **Linux:** Instala el paquete del servidor OpenSSH de tu distro. En Ubuntu, ejecuta `sudo apt install openssh-server`.
     [Guía de Ubuntu](https://ubuntu.com/server/docs/how-to/security/openssh-server/)
   - **Windows:** En **Optional features**, instala **OpenSSH Server** si no está instalado. Abre **Services**, selecciona
     **OpenSSH SSH Server** (`sshd`), cambia el tipo de inicio a **Automatic** e inicia el servicio. [Guía de Microsoft](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse?pivots=windows-11&tabs=gui)
3. Busca una dirección IP o un nombre DNS que tu teléfono pueda usar para conectarse a la máquina. Anota también tu usuario
   de SSH. En macOS, el panel de Sesión remota muestra ambos en un comando como `ssh tunombre@192.168.1.20`.

## 2. Conecta el teléfono

1. Abre Remote Shell y ve a **Configuración → Máquina**.
2. Escribe la **Dirección de la máquina** y tu **Usuario**. Deja el **Puerto** en 22, salvo que hayas cambiado el puerto de
   SSH en la máquina.
3. Si [agregas la llave del teléfono a mano](#configura-la-llave-a-mano), deja en blanco la **Contraseña de la máquina**.
   Si no, escribe la contraseña de tu cuenta de SSH. La app la usa una sola vez para agregar la llave y no la guarda.
4. Toca **Guardar y conectar**.
5. Cuando la app pregunte **¿Confiar en esta máquina?**, toca **Detalles** para ver la huella completa. [Verifica la host key](#verifica-la-host-key)
   directamente en la máquina y toca **Confiar** solo si las huellas coinciden.

Listo: tus spaces y tus agentes aparecen en la pestaña **Herdr**. Desde ahora el teléfono se conecta con su propia llave.

### Verifica la host key

En la propia máquina, ejecuta `ssh-keygen -lf` con el archivo público de la host key del mismo tipo que aparece en **Detalles**.
Por ejemplo, para Ed25519 con el path predeterminado:

- **macOS o Linux:** `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`
- **Windows (PowerShell):** `ssh-keygen -lf "$env:ProgramData\ssh\ssh_host_ed25519_key.pub"`
  ([Path de la host key en Windows](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement))

### Configura la llave a mano

En **Configuración → Máquina**, copia **La llave de este teléfono**. En la máquina, agrégala en una línea nueva sin reemplazar las
llaves que ya están ahí. Con la configuración predeterminada de OpenSSH, usa:

- **macOS o Linux:** `~/.ssh/authorized_keys` de la cuenta con la que te conectas.
- **Windows, cuenta estándar:** `%USERPROFILE%\.ssh\authorized_keys` en el perfil de esa cuenta.
- **Windows, cuenta del grupo Administrators:** `%ProgramData%\ssh\administrators_authorized_keys`.
  Sigue las [indicaciones de Microsoft sobre permisos](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh-server-configuration#authorizedkeysfile).

## 3. Cómo usarla

- **Pestaña Herdr:** tus spaces, sus tabs y paneles, y cada agente con su estado. Toca un panel para abrir su terminal.
- **En una terminal:** la barra de teclas te da Esc, Tab, Ctrl y flechas; el micrófono dicta; mantén pulsado sobre el texto y
  elige **Leer desde aquí** para escucharlo.
- **Pestaña Lector:** pega cualquier texto y escúchalo.
- **Configuración:** tema, idioma, teclas y atajos, voz de lectura y dictado.

## Úsala fuera de casa

En casa, el teléfono llega a la máquina por tu wifi. Para usarla desde cualquier lugar, pon los dos en la misma red privada
con [Tailscale](https://tailscale.com) (gratis para uso personal) u otra VPN. **No abres ningún puerto en tu router**, y
funciona aunque tu proveedor de internet comparta una IP pública entre muchos clientes (CGNAT).

1. Instala Tailscale en la máquina y en el teléfono ([descargas](https://tailscale.com/download)) y entra en los dos con la
   misma cuenta.
2. En la app de Tailscale de la máquina, anota su nombre de Tailscale (un nombre corto como `mi-mac`, o el completo que
   termina en `.ts.net`) o su dirección, que empieza con `100.`.
3. En Remote Shell, ve a **Configuración → Máquina**, escribe ese nombre o esa dirección en **Dirección de la máquina**, deja
   el puerto 22 y toca **Guardar y conectar**.
4. Deja Tailscale encendido en el teléfono mientras estás fuera. iPhone y Android solo usan una VPN a la vez.

Deja apagada la función de SSH propia de Tailscale: la app usa el servidor SSH normal de tu máquina.

**Por qué es seguro.** La máquina y el teléfono salen cada uno por su cuenta a conectarse con Tailscale, igual que un
navegador abre una página, así que nada en tu red se queda esperando conexiones desde internet. Si no logran verse directo,
el tráfico pasa por un relevo de Tailscale que no puede leerlo: sigue cifrado de punta a punta. A tu SSH solo llegan tus
propios equipos, y la app igual comprueba la identidad de la máquina y entra con la llave del teléfono.

## Si algo falla

| Lo que ves | Qué hacer |
|---|---|
| "No se puede llegar a la máquina" | Comprueba que SSH esté activo y que el teléfono pueda conectarse a la máquina desde la misma red o por tu VPN. Fuera de casa, revisa que [Tailscale](#úsala-fuera-de-casa) esté encendido en el teléfono. En iPhone, permite **Red local** para Remote Shell en Configuración. |
| "Contraseña no aceptada" | Revisa la contraseña. Si no se permite iniciar sesión por SSH con contraseña, [agrega la llave a mano](#configura-la-llave-a-mano). |
| "No se encontró herdr en la máquina" | Instala herdr en la máquina y comprueba que `hc` esté disponible para tu cuenta de SSH. En macOS o Linux, revisa el `PATH` de la cuenta y los paths de instalación comunes: `~/.local/bin`, `/opt/homebrew/bin` y `/usr/local/bin`. |
| "herdr no está abierto" | Abre herdr en la máquina y toca **Intentar de nuevo**. |
| "La identidad de esta máquina cambió" | Si no esperabas que cambiara la host key, no continúes. Si esperabas el cambio y ya [verificaste la nueva huella](#verifica-la-host-key), ve a **Configuración → Máquina**, abre **Host key** y elige **Olvidar la host key**. Toca **Guardar y conectar** y vuelve a comparar las huellas antes de tocar **Confiar**. |

Más sobre qué protege la app y cómo: [Seguridad y privacidad](security-and-privacy.es.md).
