<p align="center"><b>English</b> · <a href="getting-started.es.md">Español</a></p>

# Getting started

Setup takes a few minutes, and you only do it once.

## 1. Prepare your machine

1. Install and start [herdr](https://herdr.dev) on your macOS, Linux, or Windows machine. The app needs `hc`, herdr's
   command line tool.
2. **Turn on SSH on the machine (on a Mac: Remote Login), then enter its address and your user.**
   - **macOS:** In **System Settings → General → Sharing**, click the **Info** button next to **Remote Login** and turn it on.
     Under **Allow access for**, choose **Only these users** and allow only your own account. [Apple's guide](https://support.apple.com/guide/mac-help/mchlp1066/mac)
   - **Linux:** Install your distro's OpenSSH server package. On Ubuntu, run `sudo apt install openssh-server`.
     [Ubuntu's guide](https://ubuntu.com/server/docs/how-to/security/openssh-server/)
   - **Windows:** In **Optional features**, install **OpenSSH Server** if it isn't already installed. Open **Services**,
     select **OpenSSH SSH Server** (`sshd`), set its startup type to **Automatic**, and start it. [Microsoft's guide](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse?pivots=windows-11&tabs=gui)
3. Find the machine's IP address or a DNS name your phone can reach, and note the account name you use for SSH. On macOS,
   the Remote Login panel shows both in a command like `ssh yourname@192.168.1.20`.

## 2. Connect the phone

1. Open Remote Shell and go to **Settings → Machine**.
2. Enter the **Machine address** and your **User**. Leave **Port** at 22 unless you changed the SSH port on the machine.
3. If you [add the phone's key manually](#add-the-phones-key-manually), leave **Machine password** blank. Otherwise, enter
   your SSH account password. The app uses it once to add the phone's key and does not save it.
4. Tap **Save and connect**.
5. When the app asks **Trust this machine?**, tap **Details** to see the full fingerprint. [Check the host key](#check-the-host-key)
   directly on the machine, then tap **Trust** only if the fingerprints match.

That's it: your spaces and agents appear on the **Herdr** tab. From now on the phone connects with its own key.

### Check the host key

On the machine itself, run `ssh-keygen -lf` on the public host key that matches the key type shown in **Details**.
For example, for Ed25519 with the default location:

- **macOS or Linux:** `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`
- **Windows (PowerShell):** `ssh-keygen -lf "$env:ProgramData\ssh\ssh_host_ed25519_key.pub"`
  ([Windows host key location](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement))

### Add the phone's key manually

In **Settings → Machine**, copy **This phone's key**. On the machine, add it as a new line without replacing existing keys.
With the default OpenSSH configuration, use:

- **macOS or Linux:** `~/.ssh/authorized_keys` for the account you use to sign in.
- **Windows, standard account:** `%USERPROFILE%\.ssh\authorized_keys` in that account's profile.
- **Windows, account in the Administrators group:** `%ProgramData%\ssh\administrators_authorized_keys`.
  Follow [Microsoft's file permissions guidance](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh-server-configuration#authorizedkeysfile).

## 3. Use it

- **Herdr tab:** your spaces, their tabs and panes, and every agent with its state. Tap a pane to open its terminal.
- **In a terminal:** the key bar gives you Esc, Tab, Ctrl and arrows; the microphone dictates; hold on the text and choose
  **Read from here** to listen to it.
- **Reader tab:** paste any text and have it read aloud.
- **Settings:** theme, language, keys and shortcuts, reading voice and dictation.

## Use it away from home

At home, the phone reaches the machine over your Wi-Fi. To use it from anywhere, put both on the same private network with
[Tailscale](https://tailscale.com) (free for personal use) or another VPN. **You don't open any port on your router**, and it
works even when your internet provider shares one public IP among many customers (CGNAT).

1. Install Tailscale on the machine and on the phone ([downloads](https://tailscale.com/download)) and sign in on both with
   the same account.
2. In the Tailscale app on the machine, note its Tailscale name (a short name like `my-mac`, or the full one ending in
   `.ts.net`) or its address, which starts with `100.`.
3. In Remote Shell, go to **Settings → Machine**, enter that name or address as the **Machine address**, keep port 22 and tap
   **Save and connect**.
4. Keep Tailscale on in the phone while you are away. iPhone and Android run only one VPN at a time.

Leave Tailscale's own SSH feature off: the app uses your machine's normal SSH server.

**Why this is safe.** The machine and the phone each reach out to Tailscale on their own, the way a browser opens a web page,
so nothing on your network waits for connections from the internet. When they can't see each other directly, the traffic
goes through a Tailscale relay that cannot read it: it stays end-to-end encrypted. Your SSH is reachable only from your own
devices, and the app still checks the machine's identity and signs in with the phone's key.

## Troubleshooting

| What you see | What to do |
|---|---|
| "Machine not reachable" | Make sure SSH is running and the phone can reach the machine on the same network or through your VPN. Away from home, check that [Tailscale](#use-it-away-from-home) is on in the phone. On iPhone, allow **Local Network** for Remote Shell in the iPhone's Settings. |
| "Password not accepted" | Check the password. If SSH password sign-in is disabled, [add the phone's key manually](#add-the-phones-key-manually). |
| "herdr not found on the machine" | Install herdr on the machine and make sure `hc` is available to your SSH account. On macOS or Linux, check the account's `PATH` and common install locations: `~/.local/bin`, `/opt/homebrew/bin`, and `/usr/local/bin`. |
| "herdr is not running" | Open herdr on the machine, then tap **Try again**. |
| "This machine's identity changed" | If you weren't expecting a new host key, stop. If the change was expected and you've [verified the new fingerprint](#check-the-host-key), go to **Settings → Machine**, open **Host key**, and choose **Forget host key**. Tap **Save and connect**, then compare again before tapping **Trust**. |

More about what the app protects and how: [Security and privacy](security-and-privacy.md).
