<p align="center"><b>English</b> · <a href="getting-started.es.md">Español</a></p>

# Getting started

Setup takes a few minutes, and you only do it once.

## 1. Prepare your Mac

1. Install [herdr](https://herdr.dev) and make sure it runs. The app needs `hc`, herdr's command line tool, on the Mac.
2. Turn on **Remote Login**: System Settings → General → Sharing → Remote Login. Under "Allow access for", choose only your own
   user.
3. Find the Mac's address: on the same Remote Login screen, macOS shows a line like `ssh yourname@192.168.1.20`. The part after
   the `@` is the address, and the part before it is your user name.

## 2. Connect the phone

1. Open Remote Shell and go to **Settings → Mac**.
2. Enter the **Mac address** and your **User**. Leave **Port** at 22 unless you changed it on the Mac.
3. Enter your **Mac password**. It is used once, to add this phone's key to the Mac, and is never saved.
4. Tap **Save and connect**.
5. The app shows the Mac's fingerprint and asks **Trust this Mac?** To be sure, tap **Details** to see the full fingerprint and
   the `ssh-keygen -lf ...` command to run on the Mac, and compare. Tap **Trust**.

That's it: your spaces and agents appear on the **Herdr** tab. From now on the phone connects with its own key.

**Prefer not to type the password?** On the connection screen, copy **This phone's key** and add that line to
`~/.ssh/authorized_keys` on the Mac yourself.

## 3. Use it

- **Herdr tab:** your spaces, their tabs and panes, and every agent with its state. Tap a pane to open its terminal.
- **In a terminal:** the key bar gives you Esc, Tab, Ctrl and arrows; the microphone dictates; hold on the text and choose
  **Read from here** to listen to it.
- **Reader tab:** paste any text and have it read aloud.
- **Settings:** theme, language, keys and shortcuts, reading voice and dictation.

## Troubleshooting

| What you see | What to do |
|---|---|
| The app cannot reach the Mac | Check the phone and the Mac are on the same network (or your VPN is on) and that Remote Login is on. On iPhone, allow **Local Network** for Remote Shell in the iPhone's Settings. |
| "Password not accepted" | Type it again. The Mac must allow password sign-in for SSH. |
| "herdr not found on the Mac" | Install herdr on the Mac. The app looks for `hc` in your PATH, `~/.local/bin`, `/opt/homebrew/bin` and `/usr/local/bin`. |
| "herdr is not running" | Open herdr on the Mac, then tap **Try again**. |
| "This Mac's identity changed" | If you did not reinstall or replace the Mac, do not continue. Otherwise, tap **Forget host key** in the connection settings and trust the Mac again. |

More about what the app protects and how: [Security and privacy](security-and-privacy.md).
