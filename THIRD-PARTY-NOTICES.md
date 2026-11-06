# Third-party notices

claude-rc-windows (MIT, see [LICENSE](LICENSE)) ships with the components below. `ClaudeRc.exe`
and `ClaudeRcHost.exe` are self-contained single-file builds: each one carries the .NET runtime and
the libraries it uses. Unofficial; not affiliated with or endorsed by Anthropic or Microsoft.

| Component | Version | Used by | Licence |
|---|---|---|---|
| [.NET runtime](https://github.com/dotnet/runtime) and [WPF](https://github.com/dotnet/wpf) (including the native `wpfgfx_cor3.dll`, `PresentationNative_cor3.dll`, `PenImc_cor3.dll`, `vcruntime140_cor3.dll`, `D3DCompiler_47_cor3.dll`) | 10.0 | both exes | MIT, © .NET Foundation and Contributors; their own notices: [dotnet/runtime](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT), [dotnet/wpf](https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT) |
| [System.Diagnostics.EventLog](https://www.nuget.org/packages/System.Diagnostics.EventLog) | 9.0.2 | ClaudeRc.exe | MIT, © Microsoft Corporation |
| [xterm.js](https://github.com/xtermjs/xterm.js) (`@xterm/xterm`) | 6.0.0 | ClaudeRc.exe (console viewer) | MIT, text below |
| [xterm.js fit addon](https://github.com/xtermjs/xterm.js/tree/master/addons/addon-fit) (`@xterm/addon-fit`) | 0.11.0 | ClaudeRc.exe (console viewer) | MIT, text below |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | ClaudeRc.exe | MIT, © .NET Foundation and Contributors |
| [Microsoft.Toolkit.Uwp.Notifications](https://github.com/CommunityToolkit/WindowsCommunityToolkit) | 7.1.3 | ClaudeRc.exe (toasts) | MIT, © .NET Foundation and Contributors |
| [H.NotifyIcon.Wpf, H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) | 2.4.1 | ClaudeRc.exe (tray icon) | MIT, © its authors (havendv) |
| [TaskScheduler](https://github.com/dahall/taskscheduler) | 2.12.2 | ClaudeRc.exe (scheduled task) | MIT, © 2002-2025 David Hall |
| [Microsoft.Web.WebView2](https://aka.ms/webview) (the .NET wrappers and `WebView2Loader.dll`) | 1.0.4314.35 | ClaudeRc.exe (console viewer) | BSD-3-Clause style, © Microsoft Corporation, text below; the package's own notices: `NOTICE.txt` in the NuGet package |
| Windows SDK projection for .NET (`Microsoft.Windows.SDK.NET.dll`, [C#/WinRT](https://github.com/microsoft/CsWinRT) `WinRT.Runtime.dll`) | 10.0.17763 | ClaudeRc.exe (toasts) | [Windows SDK licence](https://aka.ms/WinSDKLicenseURL); C#/WinRT: MIT, © Microsoft Corporation |
| [Microsoft.Windows.CsWin32](https://github.com/microsoft/CsWin32) | 0.3.367 | ClaudeRcHost.exe (generated P/Invoke code; build time only) | MIT, © Microsoft Corporation |

The Microsoft Edge WebView2 Runtime itself is not included: the console viewer uses the one
installed in Windows.

## MIT License

Applies to every component above marked MIT, with its own copyright line.

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

## xterm.js

```
Copyright (c) 2017-2019, The xterm.js authors (https://github.com/xtermjs/xterm.js)
Copyright (c) 2014-2016, SourceLair Private Company (https://www.sourcelair.com)
Copyright (c) 2012-2013, Christopher Jeffrey (https://github.com/chjj/)
```

## xterm.js fit addon

```
Copyright (c) 2019, The xterm.js authors (https://github.com/xtermjs/xterm.js)
```

Both under the MIT License above; the full files are in the source tree at
`src/ClaudeRc.Tray/Viewer/web/xterm/LICENSE` and `src/ClaudeRc.Tray/Viewer/web/addon-fit/LICENSE`,
and the app serves them to its viewer at `xterm/LICENSE` and `addon-fit/LICENSE`.

## Microsoft.Web.WebView2

```
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```
