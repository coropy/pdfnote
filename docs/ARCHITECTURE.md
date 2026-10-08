# PdfNote アーキテクチャ（docs/ARCHITECTURE.md）

最終更新: v0.2 (Phase 2)

## 1. レイヤー構成

```
┌──────────────────────────────────────────────────────┐
│ UI Layer          MainWindow (WinUI 3)               │
│                  ツールバー / ダイアログ / 状態表示   │
├──────────────────────────────────────────────────────┤
│ View Layer       PdfCanvas (Win2D CanvasControl)      │
│                  GPUテクスチャの行列変換描画           │
│                  ポインタ入力 (パン/ズーム)           │
├──────────────────────────────────────────────────────┤
│ Input Layer      PdfCanvas内 ポインタルーティング     │
│                  (v0.2: Ink Layer / ペンイベント分離) │
├──────────────────────────────────────────────────────┤
│ Session Layer    PdfSession                          │
│                  現在ページ/ズーム状態と再描画要求    │
├──────────────────────────────────────────────────────┤
│ PDF Rendering    PdfDocument / PdfRenderQueue /      │
│ Layer            PageBitmapCache (LRU)               │
│                  専用バックグラウンドスレッド (PDFium) │
├──────────────────────────────────────────────────────┤
│ Storage Layer    (v0.3) .note保存 / 自動保存          │
├──────────────────────────────────────────────────────┤
│ Perf Monitoring  PerfMonitor (FPS / frame / render)  │
└──────────────────────────────────────────────────────┘
```

## 2. データフロー（v0.1）

```
[ユーザー操作]
  ページ移動 / ズーム確定 / 開く
        │  PdfSession.RequestRender(page, pxPerPt)
        ▼
  PageBitmapCache (LRU) ── hit ─▶ RenderedFrame を即時通知
        │ miss
        ▼
  PdfRenderQueue (latest-wins, 専用Thread BelowNormal)
        │  PDFium で BGRA ラスタライズ
        ▼
  RenderedFrame { byte[] BGRA, w, h, PxPerPt, RenderTime }
        │  UIスレッドへ
        ▼
  PdfCanvas.SetFrame(): CanvasBitmap (GPU) 生成 → Invalidate
        ▼
  Draw: DrawingSession.Transform = scale*translate → DrawImage
        （ズーム/パン中はこの行列だけ変更、再レンダリングなし）
```

### 各コンポーネントの責務

| コンポーネント | ファイル | 責務 | 責務外 |
|---|---|---|---|
| MainWindow | MainWindow.xaml(.cs) | UI配線・ショートカット・ダイアログ | レンダリング制御の詳細 |
| PdfSession | Session/PdfSession.cs | 現在ページ・ズーム状態、必要解像度の決定、キャッシュ/キュー呼び出し | 描画・入力 |
| PdfDocument | Core/PdfDocument.cs | Docnetラップ・ページ数・pt寸法・ラスタライズ | スレッド管理 |
| PdfRenderQueue | Core/PdfRenderQueue.cs | 直列レンダリング専用スレッド、latest-wins、計測 | キャッシュ |
| PageBitmapCache | Core/PageBitmapCache.cs | LRU+バイト予算管理 | GPU管理 |
| PdfCanvas | View/PdfCanvas.cs | GPU描画・行列・ポインタ入力・ズーム確定検知 | PDF処理 |
| PerfMonitor | Diagnostics/PerfMonitor.cs | FPS/frame/render/upload/memory計測と表示 | 制御 |

## 3. ズーム/パンの実現方式

- `PdfCanvas` は現在のGPUテクスチャ（`CanvasBitmap`）を**1枚だけ**保持
- 表示倍率 `displayPxPerPt`（DIP/pt）とテクスチャ解像度 `bitmapPxPerPt` の比率を行列スケールとして描画
- ホイール/ピンチ → 行列更新 → `Invalidate()`（GPU合成のみ、16.7ms内で完結）
- ズーム終了後350msデバウンス → `ZoomSettled` イベント → 必要なら `PdfSession` がバックグラウンド再レンダリング
- 再レンダリング要求解像度: `displayPxPerPt`（上限16M px）。乖離が15%未満ならスキップ

## 4. スレッドモデル

| スレッド | 役割 |
|---|---|
| UIスレッド | XAML / Win2D Draw / CanvasBitmap生成 / 入力 |
| Renderスレッド (BelowNormal) | PDFium ラスタライズ（直列・latest-wins） |
| XAML Composition | FPS計測 (CompositionTarget.Rendering) |

Docnetは内部の `DocLib.Lock` で全PDFium呼び出しを直列化するため、Renderスレッド1本で競合しない。

## 5. メモリ管理

- 生BGRAバッファ: LRUキャッシュ（192MB / 6枚上限）で管理、 evictは参照解除のみ（GC委譲）
- GPUテクスチャ: 表示中ページ1枚 + 移行期間の旧ページ最大1枚。ページ/解像度確定時に旧BitmapをDispose
- キャッシュ予算超過分は即時LRU削除。キャッシュクリアは文書切替時

## 6. 例外ポリシー

- `PdfDocument.Open` の失敗はカスタム例外 (`PdfOpenException`) に変換してUIへメッセージ
- Renderスレッドの例外は呼び出し元タスクに伝播し、UIで捕捉してダイアログ表示（アプリは継続）
- 予期しない例外はログ（ローカルのみ）とダイアログで通知

## 7. 拡張ポイント（過剰抽象化はしない）

- v0.3 Storage Layer: `PdfSession` の状態更新をフックしてデバウンス保存
- 先読み: `PdfSession.RequestRender` の呼び出し側に設定フラグを追加するだけで可能な設計

## 8. Ink パイプライン (v0.2)

PDF Renderer と Ink Renderer は完全に独立している。

```
Pointer (PdfCanvas)
    │  ペン: X/Y(pt), Pressure, PointerId, 種別, バレル/裏消しゴム
    ▼
InkPointerEvent ──▶ InkSession
    │                     │
    │        Input Layer  │  InkToolController (基本ツール+一時上書き)
    │        PenButtonMap ┘  InputAction: TemporaryEraser / Undo / Pan ...
    ▼
Ink Model (InkDocument ─ InkPage └ Stroke[] / readonly struct InkPoint)
    │  Add/RemoveStroke Operation (Undo/Redo 256)
    ▼
Ink Renderer
    ├─ 完成ストローク: ページ単位 Ink Texture (CanvasRenderTarget)
    └─ 描画中ストローク: セグメント直接描画
    ▼
GPU 合成 (PdfCanvas.OnDraw: PDF Texture + Ink → 1フレームに統合)
```

| コンポーネント | 責務 |
|---|---|
| PdfCanvas (Ink部) | ポインタ情報の抽出と pt 座標変換、Ink描画コールバックの起動 |
| InkPointerEvent | 入力の転送形式 (struct) |
| PenButtonMap / InkToolController | ペンボタン→InputAction→機能、基本ツール+TemporaryEraserの状態機械 |
| InkSession | Ink Model の所有、Undo/Redo、消しゴムヒットテスト、描画指示 |
| InkRenderer | Ink Texture 管理とGPU描画 (PDFに非依存) |
