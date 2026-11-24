<p align="center"><b>English</b> · <a href="security-and-privacy.es.md">Español</a></p>

# Security and privacy

Remote Shell gives your phone access to the terminals on your Mac, so it is reasonable to ask how that access is protected. This page
explains it in plain words. It also serves as the app's privacy policy.

## The short version

- Your phone talks **directly** to your Mac over SSH. There is no Remote Shell server, no account and no sign-up.
- The app has **no analytics, no tracking, no ads and no crash reporting** sent anywhere.
- Your terminal content stays between your phone and your Mac. The only exception is a cloud voice or cloud dictation service
  that **you** choose to turn on, with **your own** API key.

## How the connection works

1. **The phone has its own key.** On first use the app creates an Ed25519 SSH key pair on the phone. The private key never
   leaves the phone: it is kept in the system's secure storage (the Keychain on iPhone, encrypted storage backed by the Android
   Keystore on Android) and is excluded from backups and transfers to a new phone.
2. **You confirm the Mac.** The first time the app connects, it shows the Mac's fingerprint and asks you to trust it. From then
   on that identity is pinned: if a different machine answers at that address, the app refuses to connect and warns you. It
   accepts a new identity only if you deliberately forget the old one in the connection settings.
3. **The Mac password is used once.** To let the phone in, you type your Mac password one time. The app uses it only to add
   the phone's public key to your Mac's list of allowed keys, and only after you have confirmed the Mac's fingerprint. The
   password is never saved; the app clears its copy as soon as it has been used. After that, the phone signs in with its key.
4. **One encrypted channel.** Everything (the agents list, terminal output, your keystrokes, images you send) travels inside
   that single SSH connection to `hc`, herdr's command line tool, running under your own user on the Mac.

The app does not open any port on your phone and does not listen for incoming connections.

## What data goes where

| Data | Where it goes |
|---|---|
| Terminal content, agent states, your keystrokes and commands | Only between your phone and your Mac, encrypted by SSH. |
| Images you attach or mark and send to an agent | Copied to a temporary folder on your Mac so the agent can read them. The app clears files older than a day from that folder. |
| Dictation with the **device** option (the default) | Processed on the phone. On iPhone the app requires on-device recognition; on Android it uses the on-device recognizer. No audio leaves the phone. |
| Dictation with a **cloud** option (OpenAI, ElevenLabs, Azure, Inworld) | When you stop dictating, the recording is uploaded to that provider with your API key. If the upload fails, the recording stays on the phone until you retry or discard it. |
| Reading aloud with the **device** voice | Spoken by the phone's own voices. Nothing is sent. |
| Reading aloud with a **cloud** voice (OpenAI, ElevenLabs, Azure, Fish Audio, Inworld) | The text being read is sent to that provider, with your API key. |
| Reading aloud with **your own server** | The text is sent only to the address you entered. |
| API keys for voice and dictation services | Stored in the phone's secure storage. Sent only to their own service. |
| Settings (Mac address and user, theme, language, keys you configure) | Stored on the phone (on iPhone they are also part of your own device backups). Never sent to any Remote Shell server. |

When you use a cloud provider, its own privacy policy applies to what you send it.

## Notifications

Notices such as "agent finished" or "agent needs you" are created **on the phone** from what the Mac reports. They do not go
through Apple's or Google's push services, so they arrive while the app is open or recently in the background: the app
keeps its connection for up to 15 minutes in the background, and iOS may end it sooner.

## Permissions the app asks for

| Permission | Why |
|---|---|
| Local network (iPhone) | To reach your Mac on your Wi-Fi. |
| Microphone, speech recognition | Only while you dictate. |
| Camera | Only when you choose to take a photo to send to an agent. |
| Add to photos (iPhone) | Only when you tap "Save to photos" on an image. |
| Notifications | To tell you when an agent finishes or needs you. |
| Background audio | So a reading continues while the screen is off or another app is in front. |
| Battery exemption (Android) | So the connection, and with it agent notices, is not cut while the app is in the background. |

## Good practices on your side

- Keep Remote Login limited to your own user account on the Mac (System Settings, Sharing, Remote Login, "Allow access for").
- If you lose a phone, remove its line from `~/.ssh/authorized_keys` on your Mac. Each phone's line ends with a comment that
  names the app, so it is easy to find.
- Reach your Mac through your home network or a VPN rather than exposing SSH to the internet.

## Reporting a problem

If you find a security issue, please report it privately as described in [SECURITY.md](../SECURITY.md).
