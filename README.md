# PdfNote

学校の授業で使う、**Windows 11 + Intel N100 向けの軽量ローカルPDF手書きノートアプリ**。
クラウド・アカウント・テレメトリは一切なし。ネットワーク不要で完全ローカル動作。

現在のバージョン: **v0.4（PDFビューア + 手書き + 保存 + ペンボタン設定）**

## プロジェクト概要

- PDF教材を開き、ページ移動・ズーム・パンで閲覧する
- 将来的にペンで手書き（Ink LayerはPDFレンダリングと完全分離）
- 軽快さ・低遅延・安定性・ローカル完結・保守性を最優先

詳細は [docs/SPECIFICATION.md](docs/SPECIFICATION.md) / [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) を参照。

## 必要環境

- Windows 11（x64）
- 推奨動作環境: Intel N200 / Intel UHD Graphics / 1920x1080
- ビルド: .NET SDK 8.0 以降（Visual Studio 不要、CLIでビルド可能）

## 開発環境

- C# / .NET 8 (net8.0-windows10.0.19041.0)
- WinUI 3 (Windows App SDK 2.5.1)
- Win2D (Microsoft.Graphics.Win2D 1.4.0)
- PDFium (Docnet.Core 2.6.0 同梱)

## ビルド方法

```pwsh
dotnet build src/PdfNote/PdfNote.csproj -c Release -p:Platform=x64
```

## 実行方法

ビルド出力の実行ファイルを直接起動（非パッケージ・自己完結型配置のため
Windows App SDK ランタイムの事前インストールは不要）。

```pwsh
src/PdfNote/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/PdfNote.exe
```

## 使い方（v0.1）

| 操作 | キー |
|---|---|
| PDFを開く | Ctrl+O またはツールバー |
| ページ移動 | PageUp / PageDown / Home / End |
| ズーム | ホイール、ピンチ、+ / - |
| パン | ドラッグ（マウス/タッチ、ペンはPanアクション時） |
| 手書き | ペン先端で描画（筆圧対応） |
| 消しゴム | ツールバーの消しゴム / ペン裏ボタン (TemporaryEraser) |
| Undo / Redo | Ctrl+Z / Ctrl+Y |
| フィットに戻す | Esc |
| FPSオーバーレイ | F9 |
| ツールバー切替 | F10 |

## 使用ライブラリとライセンス

| ライブラリ | ライセンス |
|---|---|
| PDFium (Docnet.Core 2.6.0 同梱) | BSD-3-Clause |
| Docnet.Core 2.6.0 | MIT |
| Windows App SDK 2.5.1 | MIT (Microsoft) |
| Microsoft.Graphics.Win2D 1.4.0 | MIT |
| .NET 8 | MIT |

## 開発フェーズ

| Phase | 版 | 内容 |
|---|---|---|
| 0 | - | 設計文書 |
| 1 | v0.1 | PDFビューア（開く/表示/ページ移動/ズーム/パン） ✅ |
| 2 | v0.2 | 手書き（ペン/消しゴム/Undo/Redo/筆圧/ペンボタン） ✅ |
| 3 | v0.3 | .note保存/自動保存/復元/原子保存/破損処理 ✅ |
| 4 | v0.4 | ペンボタン設定UI/カスタムショートカット ✅ |
| 5 | v0.5 | N200実測による性能最適化 |
| 6 | v1.0 | 蛍光ペン/書き出し/設定 等 |

## プライバシー

通信なし・収集なし・広告なし。PDFとノートデータはすべてローカルのみ。
