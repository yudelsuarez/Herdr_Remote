#!/bin/sh
# Builds the Pane terminal bundle into HerdrRemote/Resources/Raw/wwwroot.
# The app includes the built bundle, so Node.js is only needed to regenerate it.
#
# esbuild bundles src/terminal.ts with @xterm/xterm 6.0.0, @xterm/addon-fit 0.11.0,
# and @xterm/addon-unicode11 0.9.0. The stylesheet and license come from @xterm/xterm.
#
# The app bundles these font files and licenses in wwwroot and loads them without network access:
# - fonts/JetBrainsMono-Regular.woff2, fonts/JetBrainsMono-Bold.woff2 (fonts/webfonts/ in the zip)
#   and licenses/JetBrainsMono-OFL.txt (OFL.txt in the zip):
#   JetBrains Mono v2.304, https://github.com/JetBrains/JetBrainsMono/releases/tag/v2.304
#   https://github.com/JetBrains/JetBrainsMono/releases/download/v2.304/JetBrainsMono-2.304.zip
#   sha256 6f6376c6ed2960ea8a963cd7387ec9d76e3f629125bc33d1fdcd7eb7012f7bbf
# - fonts/SymbolsNerdFontMono-Regular.ttf and licenses/NerdFontsSymbols-LICENSE.txt (LICENSE in the zip;
#   the release ships no woff2, so the TTF is bundled as is, sha256
#   fe471e538392f51910faab985fa8e192a39dd3426125edd15b71b3680df0e749):
#   Nerd Fonts v3.5.1, https://github.com/ryanoasis/nerd-fonts/releases/tag/v3.5.1
#   https://github.com/ryanoasis/nerd-fonts/releases/download/v3.5.1/NerdFontsSymbolsOnly.zip
#   sha256 fdca3682534f6f65e1ccb2345b0362ccf67d9b8eca7c8025330946e93e2473bc
# - fonts/GeistMono-Regular.woff2, fonts/GeistMono-Bold.woff2 (geist-font/GeistMono/webfonts/ in the zip)
#   and licenses/Geist-OFL.txt (geist-font/OFL.txt in the zip):
#   Geist v1.9.2, https://github.com/vercel/geist-font/releases/tag/v1.9.2
#   https://github.com/vercel/geist-font/releases/download/v1.9.2/geist-font-v1.9.2.zip
#   sha256 15f066d58dcb99fb10014a49913009fdaf41e464cfb8851a44b44e593c95d3d8
# - fonts/RemoteShellSymbolsMono-Regular.woff2 and licenses/Noto-OFL.txt: symbols-font.py builds the
#   font from Noto symbol fonts (SIL OFL 1.1) and lists its sources and their hashes.
set -eu

cd "$(dirname "$0")"
wwwroot=../HerdrRemote/Resources/Raw/wwwroot

npm ci
# Target Safari 15 syntax for WKWebView and Chrome 101 for the Android 13 (API 33) system WebView.
npx --no-install esbuild src/terminal.ts --bundle --minify --format=iife --target=safari15,chrome101 --outfile="$wwwroot/terminal.js"
cp node_modules/@xterm/xterm/css/xterm.css "$wwwroot/xterm.css"
cp node_modules/@xterm/xterm/LICENSE "$wwwroot/licenses/xterm-LICENSE.txt"
