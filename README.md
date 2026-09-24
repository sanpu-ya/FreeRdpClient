# FreeRDP Client for Windows (.NET 10 / WinUI 3)

FreeRDP 3 を RDP エンジンとして使う Windows 用リモート デスクトップ クライアントです。
UI は WinUI 3 (Windows App SDK 1.8)、アプリ本体は C# / .NET 10 で書かれています。

## 構成

```
FreeRDP-dotnet/
├─ native/FreeRdpBridge/      C で書いた薄いブリッジ DLL (FreeRDP 3 のクライアント API をラップ)
│   ├─ rdp_bridge.h / .c
│   ├─ CMakeLists.txt
│   └─ build.cmd
├─ src/FreeRdp.Interop/       P/Invoke ラッパー (RdpSession, FrameBuffer, RdpConnectionOptions)
└─ src/FreeRdpClient/         WinUI 3 アプリ
    ├─ Controls/RdpDisplay.cs    画面表示とマウス/キーボード入力
    ├─ Views/ConnectionPage      接続フォーム + 最近の接続
    ├─ Views/SessionView         接続中の画面、ツールバー、接続情報、再接続
    ├─ Input/KeyboardHook.cs     WH_KEYBOARD_LL によるキー取得 (wfreerdp と同じ方式)
    └─ Services/                 接続履歴 (JSON)、資格情報マネージャー、ダイアログ、キーボード名/種別の判定
```

```
 WinUI 3 (UI スレッド)                     FreeRDP スレッド
 ┌───────────────────────┐   P/Invoke   ┌──────────────────────────────┐
 │ RdpDisplay            │ ───────────▶ │ FreeRdpBridge.dll            │
 │  WriteableBitmap      │  入力/リサイズ │  rdpClientContext + GDI      │
 │  InputCursor(HCURSOR) │ ◀─────────── │  EndPaint / Pointer / cliprdr│
 └───────────────────────┘  コールバック └──────────────┬───────────────┘
                                                       │
                                        freerdp3.dll / freerdp-client3.dll / winpr3.dll
```

ブリッジは FreeRDP 付属クライアントの実装を参照しています。

| 機能 | 参照元 |
|---|---|
| RDP スレッドと UI スレッドの分離、PreConnect/PostConnect、イベント ループ | `client/SDL/SDL3/sdl_context.cpp` |
| キーボード (スキャンコード、NumLock/Pause/右 Shift の補正)、キーボード レイアウト検出 | `client/Windows/wf_event.c`, `wf_client.c` |
| リモート カーソル (HCURSOR 生成) | `client/Windows/wf_graphics.c` |
| チャネル (rdpgfx, disp, cliprdr) | `client/Windows/wf_channels.c`, `client/common/client.c` |

接続設定は FreeRDP のコマンドライン形式 (`/v:` `/u:` `/gfx` ...) に変換し、FreeRDP 自身のパーサーで適用しています。
そのため「追加の FreeRDP オプション」欄には `wfreerdp` / `sdl-freerdp` と同じオプションをそのまま書けます
(例: `/gateway:g:gw.example.com /microphone /drive:share,C:\share`)。
パスワードとキーボード種別/レイアウトだけは、コマンドラインを通さず設定 API (`freerdp_settings_set_value_for_name`) で直接設定します。

## 主な機能

- RDP 8 グラフィックス パイプライン (RemoteFX / Progressive、オプションで H.264 AVC444)
- 動的解像度 (ウィンドウ サイズに合わせてリモートの解像度を変更) / 固定解像度
- 物理ピクセル等倍の表示 (文字がぼやけないよう、収まる限り拡大縮小しない。詳細は「画面表示」)
- ローカルの表示スケール (DPI) に合わせたリモートのスケーリング
- タブで複数セッション、全画面表示 (Ctrl+Alt+Enter または Ctrl+Alt+Break で切り替え、上端にポインターを当てるとバーを表示)
- キーボード: Windows キー / Alt+Tab もリモートへ送信 (設定で Windows キーをローカルに残すことも可)、
  ハードウェア キーボード (英語 101/102 キー・日本語 106/109 キー) と通知レイアウトの選択 (「キーボード」参照)
- リモートのカーソル形状をそのまま表示
- クリップボード共有 (テキスト、双方向)、音声再生、ドライブのリダイレクト
- NLA / TLS、証明書の確認ダイアログ (FreeRDP の known_hosts に保存)、資格情報の入力ダイアログ
- 接続情報の表示 (ツールバーの「接続情報」。要求した設定と、ネゴシエーション後に実際に使われている値)
- 接続履歴、パスワードの保存 (Windows 資格情報マネージャー)
- .rdp ファイルを開く (コマンドライン引数 `FreeRdpClient.exe file.rdp` も可)
- 自動再接続

## 画面表示

- リモート画面は物理ピクセル等倍で表示します。表示領域より大きい場合だけ縦横比を保って縮小し、
  ツールバーに「縮小表示 xx%」と表示します (縮小すると文字はぼやけます)。
- はみ出しが 3% 以内なら縮小せず、等倍のまま端を数ピクセル切り取って表示します。
- 「ウィンドウに合わせる」の場合、ウィンドウのリサイズ中も縮小せず左上基準の等倍表示のままにし、
  サーバー側のリサイズを待ちます (一時的に端が切れる/黒い余白が出るのは mstsc と同じ動作です)。
- 横幅は 4 の倍数に切り捨ててサーバーに要求します (切り上げると表示領域を超えて縮小表示になるため)。
- xrdp は画面データのアルファ値を不定のまま送ることがあるため、フレームのアルファは常に不透明 (0xFF) にしています。

## キーボード

接続フォームの「ローカル リソース」で設定します (接続先ごとに保存)。

| 設定 | 選択肢 |
|---|---|
| ハードウェア キーボード | 自動 (Windows の設定)、日本語キーボード (106/109 キー)、英語キーボード (101/102 キー) |
| リモートに通知するキーボード レイアウト | 自動 (Windows の入力言語)、日本語、英語 (US) |

- 「自動」のハードウェア キーボードは、Windows の 設定 → 時刻と言語 → 言語 → オプション →
  ハードウェア キーボード レイアウト の値 (レジストリ `i8042prt\Parameters` の `LayerDriver JPN` /
  `OverrideKeyboardType` / `OverrideKeyboardSubtype`) を読み取ります。
- 日本語レイアウトでは、英語キーボード = 種別 7 / サブタイプ 0、日本語キーボード = 種別 7 / サブタイプ 2 を通知します
  (mstsc と同じ)。Windows サーバーはこれで正しい配列になります。
- **xrdp (Linux) は通知レイアウトだけで X の配列を決め、サブタイプを見ません。**
  英語キーボードで記号 (`@` `[` `]` `:` など) がずれる場合は、通知レイアウトを「英語 (US)」にしてください。
- 接続中は「接続情報」でキーボード レイアウト名と種別を確認できます。

## ビルド

前提:

- Visual Studio 2026 (C++ デスクトップ開発、CMake、Ninja)
- .NET 10 SDK
- ビルド済みの FreeRDP 3 (`..\FreeRDP\build\install` に `bin`, `include`, `lib` があること)
  - このリポジトリでは `FreeRDP\build\build-win64.cmd` で作成したものを使用しています (vcpkg の依存 DLL も `bin` にコピー済み)

手順:

```bat
rem 1. ブリッジ DLL をビルド (FreeRDP の場所が違う場合は引数で install ディレクトリを指定)
native\FreeRdpBridge\build.cmd

rem 2. アプリをビルド
dotnet build FreeRdpClient.slnx -c Release -p:Platform=x64
```

出力: `src\FreeRdpClient\bin\Release\net10.0-windows10.0.19041.0\win-x64\FreeRdpClient.exe`

- ビルド時に FreeRDP の DLL (`FreeRDP\build\install\bin\*.dll`、SDL3 を除く) と `FreeRdpBridge.dll` が出力フォルダにコピーされます。
  場所を変える場合は MSBuild プロパティ `FreeRdpBinDir` / `FreeRdpBridgeDir` を指定してください。
- アプリの起動中は出力フォルダの DLL がロックされるため、Release ビルドの前にアプリを閉じてください。
- `rdp_bridge.c` を変更したときは、手順 1 からやり直してください (手順 2 で出力フォルダにコピーされます)。
- Windows App SDK は自己完結 (`WindowsAppSDKSelfContained`) なので、ランタイムのインストールは不要です。

## データの保存場所

| 内容 | 場所 |
|---|---|
| 接続履歴 | `%LOCALAPPDATA%\FreeRdpClient\connections.json` |
| パスワード | Windows 資格情報マネージャー (`FreeRdpClient/<host>:<port>/<user>`) |
| FreeRDP のログ | `%LOCALAPPDATA%\FreeRdpClient\logs\freerdp-YYYYMMDD.log` |
| 信頼した証明書 | FreeRDP の設定フォルダ (`%APPDATA%\FreeRDP\server`) |

MSIX パッケージ化されたアプリ (Claude デスクトップなど) から起動した場合、`%LOCALAPPDATA%` への書き込みは
そのアプリのパッケージ領域 (`%LOCALAPPDATA%\Packages\<パッケージ>\LocalCache\Local\...`) に仮想化されます。

## トラブルシューティング

| 症状 | 対処 |
|---|---|
| 接続できない / 原因が分からない | 画面のエラーメッセージと FreeRDP のログを確認。`license_read_binary_blob_data` の ERROR は xrdp で毎回出るもので無害 |
| 文字がぼやける | ツールバーに「縮小表示」が出ていれば、解像度を「ウィンドウに合わせる」にする。出ていない場合はリモート側のフォント設定 (アンチエイリアスを「グレースケール」、ヒンティングを「完全」など) を確認 |
| 記号キーが刻印と違う | 「ハードウェア キーボード」と「リモートに通知するキーボード レイアウト」を確認 (xrdp では「英語 (US)」) |
| Linux のターミナルで Ctrl+C を押すと `^C` と出る | ターミナルの仕様。コピーは Ctrl+Shift+C |

## 実装メモ (FreeRDP / WinUI の注意点)

- `freerdp_connect()` は PreConnect の後、日本語レイアウト (0x411) なら無条件にキーボード種別を 7 / サブタイプ 2 に上書きします。
  ブリッジはクライアント コア データ送信直前 (`CONNECTION_STATE_MCS_CREATE_REQUEST` への遷移) で指定値に書き戻します。
  その際に使う `ConnectionStateChange` イベントは libfreerdp が発行するものの登録していないため、ブリッジが自分で登録しています。
- `/kbd:subtype:` は 0 を受け付けない (最小値 1) ため、キーボード種別は設定 API で設定しています。
- WinUI の `TabView` は、選択中タブの `TabViewItem.Content` を差し替えても表示が更新されません。各タブに固定の `Border` を置き、その子要素を差し替えています。
- `ContentControl` の既定テンプレートは `Background` を描画しません。リモート画面の下地は明示的な黒い `Grid` です。
- リモートのカーソルは `IInputCursorStaticsInterop::CreateFromHCursor` (Windows App SDK) で `InputCursor` に変換しています。

## 動作確認状況

xrdp (Linux Mint) のサーバーに対して、接続・表示・クリップボード・キーボード (英語キーボード + 「英語 (US)」)・動的解像度・全画面表示を確認済みです。
Windows サーバーへの接続、音声、日本語入力、再接続は未確認です。

## 制限事項

- クリップボードはテキストのみ (ファイル・画像は未対応)
- マルチモニター、RemoteApp (RAIL)、スマートカード選択 UI は未実装
- 描画は `WriteableBitmap` への CPU コピーです。4K など高解像度では Direct3D (SwapChainPanel) 化の余地があります
