# Contributing

Thanks for your interest in Remote Shell. Bug reports, ideas and pull requests are welcome.

- **Bugs and ideas:** open an issue. Include the app version, the platform (iPhone or Android) and the steps to see the problem.
- **Security problems:** do not open an issue; follow [SECURITY.md](SECURITY.md).

## Before you send a pull request

- Build both platforms with no new warnings (`dotnet build -f net10.0-android` and `-f net10.0-ios`). See the README for setup
  and for signing a device build.
- iPhone and Android must behave the same. A change that only works on one platform is not finished.
- Agent support is generic: a change for one agent CLI (Claude Code, Codex, OpenCode, pi, ...) must not change how the others
  behave.
- Code and comments are in English. User-visible text needs its Spanish line in
  `HerdrRemote/HerdrRemote/Languages/es-US_Español.txt`.
- If you change the terminal (`HerdrRemote/terminal-web/src`), rebuild the bundle with `./build.sh` in that folder.
- Keep pull requests small and focused, and explain what you tested and on which device.

By contributing, you agree that your contribution is licensed under the [Apache License 2.0](LICENSE).
