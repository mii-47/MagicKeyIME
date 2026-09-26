# Magic Keyboard 英数/かな 有効化

Apple Magic Keyboard(JIS)をWindowsでBluetooth接続したときに反応しない
「英数」「かな」キーを有効化し、IMEの切り替えに使えるようにする。

- 英数 → IME OFF(半角英数)
- かな → IME ON(日本語)

## 使い方(タスクトレイ)

`MagicKeyIME.exe` を実行(管理者権限が必要)。  
タスクトレイに「M」アイコンが常駐します。  

| メニュー | 動作 |
|---|---|
| 状態: … | 現在の状態(適用済み / 未適用)を表示 |
| 英数/かなを有効化(適用) | 記述子を書き換えて有効化。実行後に再起動を促す |
| 元に戻す(解除) | 原本(ProgramDataのバックアップ)から書き戻す |
| Windows起動時に常駐 | ログオン時の自動起動を登録/解除 |
| 終了 | 常駐を終了 |

適用・解除の後は**再起動**で反映。  
アイコンは適用済みで緑、未適用で灰色。

## 仕組み

キーボードがOSに渡すHIDレポート記述子が、キー番号の上限を`0x65`に制限しており、
英数(`0x91`)/ かな(`0x90`)は範囲外として破棄されています。  
Bluetoothではその記述子が**SDP(Service Discovery Protocol)**のレコードとしてレジストリにキャッシュされます。  
アプリはこれを走査し、上限を`0x65`→`0xE7`に拡張します。  
(論理最大値は符号の都合で 2 バイト `26 E7 00` に変換、SDPの入れ子の長さフィールドは記述子構造を解析して自動修正)。

## ファイルと保存場所

| 場所 | 内容 |
|---|---|
| `src/`(`MagicKeyIME.cs` / `MagicKeyIME.csproj` / `MagicKeyIME.manifest`) | ソース(.NET 10 プロジェクト) |
| `src/bin/Release/net10.0-windows/MagicKeyIME.exe` | ビルド成果物(本体)。任意の場所へ置いて実行可 |
| `C:\ProgramData\MagicKeyIME\sdp-backup.txt` | 原本のバックアップ(全PC共通の固定場所) |

## ビルド

[.NET 10 SDK](https://dotnet.microsoft.com/download) が必要です。

```
dotnet build src/MagicKeyIME.csproj -c Release
```

出力: `src/bin/Release/net10.0-windows/MagicKeyIME.exe`(実行には .NET 10 ランタイムが必要)。

他PCへ配布するなど、ランタイム非依存の単一 exe にする場合:

```
dotnet publish src/MagicKeyIME.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## 注意

- **再ペアリングすると元に戻ります**  
Windowsがキャッシュを取り直すため、再度「適用」してください。
- **有線(USB)接続は別経路**なので効きません(Bluetooth用)。
