# Notebook アーキテクチャ移行設計（docs/NOTEBOOK_ARCHITECTURE.md）

最終更新: 段階1 (設計書・データモデル案・保存形式・移行計画)

## 0. この文書の位置づけ

次期設計変更「PDFビューアー → ノートブックアプリ」への移行における、**段階1の成果物**。
既存コードの調査結果、推奨データモデル、保存形式案、旧 `.note` からの移行計画、
変更ファイル一覧、段階的実装計画、リスクと回帰テスト、本次期で実装しない機能を定める。

本書は設計案であり、実装の全面改修は含まない。各段階の承認後に実装を進める。

---

## 1. 現状コードの調査結果

### 1-1. PDFページと InkDocument / InkPage / Stroke の関係

現行の Ink モデル (`Ink/InkModel.cs`) は「開いている PDF のページ番号」に強く紐づいている。

```
InkDocument (Dictionary<int, InkPage>)
  └─ InkPage { PageIndex: int, List<Stroke> }   ← PageIndex = PDFページ番号 (0始まり)
       └─ Stroke { PageIndex: int, Points: List<InkPoint>, WidthPt, Color, Tool, Timestamp }
```

- `Stroke` が自ら `PageIndex` を保持する。ストロークと PDF ページの関連付けは int の一致のみ。
- `NoteSerializer.ToInkDocument(file, currentPdfPageCount)` は PDF ページ数範囲外のページを破棄する。
  → **手書きデータの存在範囲が PDF ページ数に固定**されている。
- `InkSession` は `_currentPage` (int) を保持し、`SetPage(page, wPt, hPt)` でページ幾何を設定。
  ページ移動時は旧ページの Ink Texture を `InkRenderer.RemovePage` で破棄する。
- `InkSession.SetPage` はページ移動時に実行中ストロークをコミットせず破棄 (`EndActiveStroke(commit: false)`)。

### 1-2. ページ座標と画面座標の変換

変換は `View/PdfCanvas.cs` に一元化されている。

| 変換 | 式 | 実装箇所 |
|---|---|---|
| 画面 → ページ | `(pointerPos - _panOffset) / displayPxPerPt` | `DispatchInkEvent` (InkPointerEvent の XPt/YPt 生成) |
| ページ → 画面 | `Transform = scale(displayPxPerPt) × translate(_panOffset)` | `OnDraw` |
| Ink Texture → ページ空間 | `scale(1 / entry.PxPerPt) × baseTransform` | `InkRenderer.Draw` (視差対策済み) |

- ストローク座標は **ページ左上原点の pt 空間**で保持され、ズーム/パン/再レンダリングに影響されない
  (SPECIFICATION §8.2)。この性質は新しい Notebook モデルでもそのまま利用できる。
- Ink 入力はペンのみ `PdfCanvas` → `InkPointerEvent` (struct) → `InkSession` に転送される。
  変換は UI スレッドで完結し、ポイント単位のオブジェクト生成はない (`InkPoint` は readonly struct)。

### 1-3. PDFレンダリングと Ink Renderer の依存関係

**両者は完全に分離済み** (ARCHITECTURE §8)。この分離が Notebook 移行の基盤になる。

```
[PDF側]  PdfDocument (Docnet/PDFium, 白背景合成済みBGRA)
             → PdfRenderQueue (専用Thread BelowNormal, latest-wins)
             → PageBitmapCache (LRU 6枚 / 192MB)
             → RenderedFrame { BGRA, w, h, PxPerPt, RenderMs }
             → PdfCanvas.SetFrame (CanvasBitmap 1枚のみ保持)
[Ink側]  InkRenderer … RenderScale 定数とページpt寸法のみに依存。PDFクラスを一切参照しない
[合成]   PdfCanvas.OnDraw: PDFテクスチャ → ページ枠 → Ink の順に 1フレームへ統合
```

重要な再利用ポイント: `PdfSession` は「1つの PdfDocument + PdfRenderQueue + PageBitmapCache」の束である。
**PDFリソースごとにこの束を 1 個生成すれば、レンダリング層 (PdfDocument / PdfRenderQueue /
PageBitmapCache / RenderedFrame / RenderScale) は無変更で再利用できる。**
`PageBitmapCache` のキーは `pageIndex` (int) だが、束ごとに分離すれば複数 PDF 間でキー衝突しない。

### 1-4. `.note` の保存・復元の仕組みと旧形式との互換性

| 項目 | 現行実装 |
|---|---|
| 保存場所 | PDF と同じフォルダの `<PDF名>.note` (`Path.ChangeExtension`) |
| 形式 | JSON v1 (`formatVersion` 必須、欠損は不明バージョンとして拒否) |
| 関連付け情報 | `pdf { fileName, fileSize, lastWriteTimeUtc, pageCount }` → 不一致時は警告のみ |
| ストローク | `points` = `[x, y, pressure, ...]` のフラット double 配列 (ページpt座標) |
| 保存パス | UI スレッドでスナップショット (`CaptureSnapshot`) → バックグラウンドで JSON 化 + 原子保存 |
| 原子保存 | `.tmp` 書き出し → `Flush(flushToDisk)` → `File.Replace` (旧ファイルは `.bak` に 1 世代退避) |
| 自動保存 | Dirty → デバウンス 800ms → 同時保存 1 件制限 → 保存中の変更は完了後 200ms で再保存 |
| 終了時 | 同期的に確定保存 (`SaveNowSync`) |
| 破損処理 | 検証項目 (有限座標 / 筆圧 0..1 / 幅 0.1..50pt / 点数上限 10 万点 / pageIndex 範囲) でストローク・ページ単位に破棄。`.bak` から復元を試行。旧データは削除しない |

アーキテクチャ上の重要点: **メモリモデル (InkDocument) とストレージ形式 (NoteDtos / NoteSerializer)
は既に分離されている**。Notebook v2 形式への拡張はこの境界で行い、v1 リーダーは読み取り専用で保持する。

### 1-5. ページ切替・Undo/Redo・消しゴム・自動保存の依存関係

```
PdfSession.SetPage ─CurrentPageChanged→ MainWindow ─→ _ink.SetPage(page, wPt, hPt)
                                                    └→ _canvas.SetPageGeometry(wPt, hPt)
PdfSession.FrameReady ─→ MainWindow (DispatcherQueue) ─→ _canvas.SetFrame
InkSession.Undo/Redo = IInkOperation (AddStroke / RemoveStroke) → InkUndoStack (上限 256 操作)
InkSession.Eraser    = InkHitTest (Stroke 単位) → RemoveStrokeOperation
InkSession.ContentChanged ─→ NoteAutoSave.OnContentChanged (デバウンス 800ms)
                            → _ink.CaptureSnapshot (UI スレッド) → 保存 (バックグラウンド)
```

- ページ移動は `PdfSession.NextPage / PreviousPage` が **PDF ページ数にクランプ**する。
  → Notebook ページ数への差し替えが必要。
- Undo/Redo・消しゴム・自動保存トリガーは `InkDocument` のみに依存し、PDF に直接依存しない。
  → 操作モデルは Notebook 化してもほぼそのまま使える。
- `InkSession` は `PdfCanvas` への直接参照 (`Invalidate` / `PanBy` / `DisplayPxPerPt`) を持つ。
  この依存は残し、キャンバスの rename / 拡張のみで対応する。

### 1-6. Notebook / Page / Object モデル移行時の変更対象

| 変更の大きさ | 対象 | 内容 |
|---|---|---|
| 大 | `View/PdfCanvas.cs` | 描画対象が「PDF 1 枚」→「背景 + オブジェクト群 + Ink」に拡張。実質 NotebookCanvas 化。行列・パン・ピンチ・ペンルーティング・ズーム確定ロジックは流用 |
| 大 | `Session/PdfSession.cs` | UI 直結のセッションとしては置換。ノートブックページ巡回 + PDF オブジェクト描画要求に分解 |
| 大 | `Storage/*` | v2 シリアライザ (NotebookDtos / NotebookSerializer / NotebookAutoSave) を新設。v1 リーダーは保持 |
| 中 | `InkSession.cs` | ページ供給源を PDF → NotebookPage へ。`ResetDocument / LoadDocument` の対象を Notebook へ |
| 中 | `MainWindow.xaml(.cs)` | 配線変更 + ホーム画面入口 |
| 中 | `InkModel.cs` | `Stroke.PageIndex` の意味変更 (PDF ページ番号 → ノートブックページ番号)。または Stroke からページ参照を外し NotebookPage が保持 |
| 小 | `InkRenderer.cs` | ページキー (int 維持可) とページ寸法の供給源を変更のみ |
| 小 | `PdfNote.csproj` | 新規ファイル追加のみ |

### 1-7. 既存実装をそのまま再利用できる部分

| ファイル | 判定 | 備考 |
|---|---|---|
| `Core/PdfDocument.cs` | ✅ 無変更 | Docnet ラップ・pt 寸法・ラスタライズ・白合成 |
| `Core/PdfRenderQueue.cs` | ✅ 無変更 | 専用スレッド・latest-wins |
| `Core/PageBitmapCache.cs` | ✅ 無変更 | LRU + バイト予算 |
| `Core/RenderedFrame.cs` | ✅ 無変更 | |
| `Core/RenderScale.cs` | ✅ 無変更 | px/pt 定数・16M px 予算クランプ |
| `Ink/InkRibbon.cs` | ✅ 無変更 | 可変幅リボン描画 |
| `Ink/InkHitTest.cs` | ✅ 無変更 | Stroke 単位ヒットテスト |
| `Ink/InkOperations.cs` | ✅ 基本無変更 | Add/RemoveStroke は流用。オブジェクト操作は将来追加 |
| `Ink/InkModel.cs` | 🔸 僅少変更 | Stroke のページ参照の意味のみ |
| `Input/*` (InkPointerEvent / InkToolController / PenButtonMap / InputAction) | ✅ 無変更 | ペンボタン・一時ツールの状態機械 |
| `Diagnostics/PerfMonitor.cs` | ✅ 無変更 | |
| `Settings/*` (AppSettings / AppSettingsStore / AppCommands / SettingsDialog) | ✅ 無変更 | |
| `Storage/NoteAutoSave.cs` のパイプライン構造 | 🔸 流用 | デバウンス・単一保存・原子置換・`.bak` 復元の枠組みを Notebook 用に流用 |
| `Storage/NoteSerializer.cs` (v1) | ✅ 保持 | 旧 `.note` 読み取り専用として維持 |
| `View/PdfCanvas.cs` の入力・行列ロジック | 🔸 流用 | 描画パスのみ拡張 |

### 1-8. 移行によって破損する可能性がある部分

1. **`Stroke.PageIndex` の意味混同**: 現在は PDF ページ番号。移行時は 1:1 対応 (PDF ページ i →
   NotebookPage i) で保存し、以後は NotebookPage のリスト順で管理する。座標系はページ左上原点 pt
   で共通のため座標変換は不要。
2. **ページキャッシュキーの衝突**: 複数 PDF リソース参照時に `pageIndex` (int) が衝突し得る。
   → PDF リソースごとに独立した「Session 束」(PdfDocument + Queue + Cache) で回避。キャッシュ実装は無変更。
3. **PDF オブジェクト移動とストローク座標の混同**: オブジェクトの変換 (位置/拡縮/回転) をストロークに
   適用してしまうと書き込み位置がずれる。→ 設計原則「ストロークは常にページ空間。PDF オブジェクトの
   変換はオブジェクト自身の変換にのみ適用」を明文化して回避。
4. **ペン入力中の重い処理**: 既存の禁止ルール (SPECIFICATION §5: UI スレッド PDF レンダリング禁止、
   ペン入力トリガーの再レンダリング禁止、ストロークごとの同期 I/O 禁止) をそのまま維持。
   オブジェクト追加時も UI スレッドで PDF を開かない。
5. **旧 `.note` 移行でのストローク欠落**: 移行はコピー型 (元ファイル不変・削除しない) + 移行後検証
   (ストローク数・座標一致) を必須化。
6. **終了時保存・`.bak` 復元**: NotebookAutoSave に差し替える際、`SaveNowSync` と Closed ハンドラの
   配線を漏らさないこと (現行は `MainWindow.Closed` で同期保存)。
7. **`InkRenderer.RemovePage` によるテクスチャ破棄**: Notebook ではページ順が不変のため、ページ移動時に
   破棄しない (無駄な再構築防止) 方針へ。メモリ上限は既存の 16M px / ページ予算で管理。

---

## 2. 推奨データモデルと責務

### 2-1. モデル

```
Notebook
 ├─ Id: Guid
 ├─ Name: string
 ├─ CreatedUtc / UpdatedUtc
 ├─ FormatVersion: int (= 2)
 ├─ Resources: List<Resource>            ← PDF / 画像の実体参照 (1 回だけ記録 = 重複保存なし)
 │    └─ Resource { Id, Kind (Pdf | Image), FileName (assets 内の相対パス),
 │                   FileSize, LastWriteTimeUtc }
 └─ Pages: List<NotebookPage>            ← 表示順 = リスト順
      └─ NotebookPage { Id, WidthPt, HeightPt, Background, Objects[], Strokes[] }
           ├─ Background { Kind (None | Plain | Grid | Lines), GridSpacingPt 等 }
           ├─ Objects (Z 順に描画。Z 同値はリスト順)
           │    ├─ PdfObject   { ResourceId, PdfPageIndex, X, Y (pt), WidthPt, HeightPt,
           │    │                RotationDeg (0/90/180/270 を第一支援), Z }
           │    └─ ImageObject { ResourceId, X, Y, WidthPt, HeightPt, RotationDeg, Z }
           └─ Strokes (既存 Ink モデルを再利用。ページ左上原点の pt 座標)
```

### 2-2. 責務分離の原則

1. **Stroke はページに属し、PDF オブジェクトに属さない。**
   Ink-on-object 結合 (ストロークをオブジェクトに追従させる) は本次期未実施。座標系の複雑さを避ける。
2. **PDF 実体は Resources に 1 回のみ記録。** PdfObject は ResourceId + PDF ページ番号 + 配置変換のみ保持。
   ページごとに PDF を埋め込まない。
3. **オブジェクトの変換とストローク座標の分離。** PDF オブジェクトを移動・拡縮しても、ストローク座標
   (ページ空間) は一切変化しない。既存ストロークはそのまま残る。
4. **Ink は常に最前面に合成** (v1 仕様)。Z 順への参加は将来課題。
5. **保存形式と内部モデルは分離** (NotebookDtos vs Notebook モデル)。既存の分離方針を踏襲。

### 2-3. PDF レンダリング要求の新規ロジック

PDF オブジェクトはページ上で変換されるため、必要テクスチャ解像度は次式で決める。

```
実効 pxPerPt = displayPxPerPt × (オブジェクト幅 pt / PDF ページ幅 pt)
```

- 求まった pxPerPt をそのまま既存 `PdfDocument.RenderPage(pageIndex, pxPerPt, ct)` に渡す。
- 16M px 予算と 1 辺 8192px 上限のクランプは既存 `RenderScale` をそのまま使用。
- それ以外 (latest-wins キュー、LRU キャッシュ、350ms デバウンス再レンダリング) は既存機構の再利用。

---

## 3. 保存形式の案 (v2)

### 3-1. コンテナ方針: 1 ノートブック = 1 フォルダ

```
Notebooks/
  └─ <ノート名>-<短ID>/
       ├─ notebook.json      ← メタデータ + Resources + Pages (背景/オブジェクト/ストローク)
       └─ assets/            ← インポート時にコピーした PDF / 画像
            └─ <短ID>.pdf
```

### 3-2. 外部参照 vs 同梱の比較

| 方式 | 利点 | 欠点 | 対策 |
|---|---|---|---|
| 外部参照のみ | ノートが軽い。ディスク重複なし | PDF の移動/削除で参照切れ | 相対パス + 指紋 (size / mtime / pageCount) 記録 + 再リンク UI |
| assets 同梱 (コピー) | 参照切れが起きない。ローカル完結と整合 | ディスクを二重使用 | PDF は 1 ノート 1 回のみコピー (ページごとの埋め込みではない)。元 PDF は不変 |

**推奨: 既定は assets 同梱** (破損耐性優先)。将来「リンク」モードを拡張として残し、
その際の参照切れ対策 (相対パス + 指紋照合 + 再リンク) は本設計に明記しておく。

### 3-3. notebook.json (formatVersion = 2)

- ルート: `formatVersion` / `notebook { id, name, createdUtc, updatedUtc }` / `resources[]` / `pages[]`
- ページ: `id` / `widthPt` / `heightPt` / `background { kind, spacingPt }` / `objects[]` / `strokes[]`
- ストローク: v1 と同じフラット配列 `[x, y, pressure, ...]` (ページ pt 座標)。
  検証項目 (有限性 / 筆圧 / 幅 / 点数上限) も v1 のロジックを流用。
- 大きな PDF / 画像データはメタデータ JSON に一切複製しない (ファイル名と指紋のみ)。

### 3-4. 保存インフラの再利用

`NoteAutoSave` の枠組みを Notebook 用に流用:

- Dirty → デバウンス 800ms → UI スレッドでスナップショット → バックグラウンドで JSON 化
- `.tmp` → `Flush(flushToDisk)` → `File.Replace` (`.bak` 1 世代)
- 同時保存 1 件制限、保存中変更の完了後 200ms 再保存、終了時同期保存
- 破損時は `.bak` から復元、復元不能でも既存データを削除しない

---

## 4. 旧 `.note` からの移行方法

- 「PDF からノートを作成」実行時に同フォルダに `<PDF名>.note` (v1) が存在したら変換を提案する。
- 変換内容 (常にコピー型。元ファイルは不変・削除・上書きしない):
  1. PDF を新ノートの `assets/` にコピー
  2. ノートページ寸法 = PDF ページ寸法、PDF ページ 1 枚ごとに NotebookPage 1 枚 + 全面 PdfObject を作成
  3. ストロークをそのままコピー (PageIndex 1:1、座標系共通のため変換なし)
- 照合は `pdf` 指紋 (fileName + fileSize + pageCount) で行い、不一致時は警告して続行可否を確認。
- **変換成功とユーザーデータの保全を確認するまで旧 `.note` を削除・上書きしない。**
- v1 リーダー (`NoteSerializer`) は将来も保持し、旧ファイルは常に読み取り可能にする。

---

## 5. 具体的な変更ファイル一覧

### 新設

| ファイル | 責務 |
|---|---|
| `Core/Notebook.cs` | Notebook / NotebookPage / PageBackground / PdfObject / ImageObject / Resource の内部モデル |
| `Session/NotebookSession.cs` | ノートブック状態・ページ巡回・Undo/Redo 発火の統合 (PdfSession の UI 直結役を置換) |
| `Session/PdfResourceRenderService.cs` | ResourceId → (PdfDocument + PdfRenderQueue + PageBitmapCache) の束を遅延生成 / LRU 管理 |
| `Storage/NotebookDtos.cs` | v2 DTO |
| `Storage/NotebookSerializer.cs` | モデル ⇄ DTO、検証、原子保存 |
| `Storage/NotebookAutoSave.cs` | NoteAutoSave の枠組みを Notebook 用に流用 |
| `Storage/NotebookLibrary.cs` | ノート一覧・作成・名前変更・削除確認・最近開いた順 |
| `Migration/LegacyNoteMigrator.cs` | v1 `.note` → Notebook 変換 (コピー型) |
| `Home/HomeView.xaml(.cs)` または `HomeWindow` | ホーム画面 (ローカル一覧) |
| `View/NotebookCanvas.cs` | PdfCanvas から派生 / 改称。背景・オブジェクト・Ink の合成 |

### 変更

| ファイル | 内容 |
|---|---|
| `Ink/InkModel.cs` | Stroke のページ参照の意味変更 (または NotebookPage 側へ移管) |
| `Ink/InkSession.cs` | ページ供給源を PDF → NotebookPage。ResetDocument / LoadDocument の対象を Notebook へ |
| `Ink/InkRenderer.cs` | ページ移動時のテクスチャ破棄方針の見直し (僅少) |
| `MainWindow.xaml(.cs)` | 配線変更 + ホーム画面入口 |
| `PdfNote.csproj` | 新規ファイル追加 |

### 無変更 (再利用)

`Core/PdfDocument.cs` / `Core/PdfRenderQueue.cs` / `Core/PageBitmapCache.cs` /
`Core/RenderedFrame.cs` / `Core/RenderScale.cs` / `Ink/InkRibbon.cs` / `Ink/InkHitTest.cs` /
`Input/*` 全体 / `Diagnostics/PerfMonitor.cs` / `Settings/*` 全体

---

## 6. 段階的な実装計画

| 段階 | 内容 | 完了条件 |
|---|---|---|
| 1 | 本設計書・データモデル案・保存形式・移行計画 | 承認 |
| 2 | 白紙ページ + 既存 Ink Renderer の統合 (NotebookSession 最小実装、白紙/方眼背景描画) | ペン・消しゴム・Undo・自動保存が現行同等の FPS / 遅延で動作 |
| 3 | notebook.json 保存・復元 (v2) | 保存 / 再起動復元 / 破損時 `.bak` 復帰が通る |
| 4 | ホーム画面 (白紙作成 / PDF から作成 / 開く / 名前変更 / 削除確認 / 最近) | 既存 PDF を直接変更しないまま一覧操作が完結 |
| 5 | PDF をページ上オブジェクトとして配置 (assets 同梱、移動・拡縮・回転・Z 順、実効 pxPerPt) | オブジェクト移動・拡縮中にストローク座標不変、FPS 回帰なし |
| 6 | 旧 `.note` の読み込みと移行 (コピー型) | 移行前後でストローク数・座標一致を検証。旧ファイル不変 |

- 各段階でビルドとテストを行い、**常に動作する状態を維持**する。
- 一度に全面改修しない。段階ごとに実装範囲を明確に区切る。

---

## 7. 想定リスクと回帰テスト

### 7-1. 回帰テスト対象 (既存機能を回帰させない)

| 項目 | 判定基準 |
|---|---|
| ペン入力→描画反映 | 目安 30ms 以内 (PerfMonitor の pen latency 計測) |
| 通常閲覧 / パン / ズーム / ペン描画 | 60 FPS |
| ページ切替 | キャッシュヒット時は即時、miss 時は latest-wins レンダリング |
| ズーム確定後の再レンダリング | 350ms デバウンス、乖離 15% 以上のみ |
| 自動保存 / 終了時保存 | デバウンス 800ms / 同期保存が漏れなく動作 |
| Undo / Redo | 256 操作上限、ページをまたぐ整合 |
| 消しゴム | Stroke 単位消去、Undo 可能 |
| `.bak` 復元 / 破損 JSON 耐性 | クラッシュせず警告表示、データを失わない |

### 7-2. 新規リスク

| リスク | 対策 |
|---|---|
| 複数 PDF リソース同時オープンでのメモリ増 | リソース束 (Document+Queue+Cache) の LRU 管理で同時オープン数に上限 |
| オブジェクト拡縮時の再レンダリング負荷 | ズーム確定と同じデバウンス方式を適用 |
| 回転オブジェクトと Ink の合成順序 | 回転は 0/90/180/270 に限定し、合成順を Ink 最前面に固定 |
| 保存形式 v2 の破損 | v1 と同じ原子保存 + `.bak` + 検証ロジックを流用 |
| 移行時のストローク欠落 | 移行はコピー型 + 移行後検証 (ストローク数・座標一致) を必須化 |

### 7-3. 性能計測方針

- **計測せずに性能が改善したと主張しない。** 各段階完了時に PerfMonitor の実測値
  (FPS / frame time / PDF render / upload / pen latency / save ms) を記録し、段階開始前と比較する。
- 比較は N200 + Intel UHD 実機での計測を要とする。

---

## 8. 必須の設計原則と本設計での扱い

| 原則 | 本設計での扱い |
|---|---|
| PDF 描画と Ink 描画を分離する | 既存の分離構造を維持。InkRenderer は PDF 非依存のまま |
| 既存の低遅延ペン入力を維持する | InkPointerEvent (struct) / InkSession の入力パスを無変更で維持 |
| ペン入力中に同期ファイル I/O や大規模なメモリ確保を行わない | 既存禁止ルール (SPECIFICATION §5) をそのまま継承 |
| Undo/Redo はページ上の編集操作と整合する | IInkOperation (Add/RemoveStroke) を Notebook ページに対応させ、オブジェクト操作は将来段階で追加 |
| オブジェクトの移動・拡大縮小とストローク座標を混同しない | ストロークは常にページ空間。オブジェクト変換はオブジェクト自身にのみ適用 |
| 保存形式と内部モデルを分離する | NotebookDtos と Notebook モデルを分離 (既存方針の踏襲) |
| ローカルファイルだけで完結する | クラウド / ログイン / 同期 / テレメトリ導入なし |
| 計測せずに性能が改善したと主張しない | §7-3 の性能計測方針 |

---

## 9. この段階で実装しない機能

- 無限キャンバス (固定用紙サイズのみ)
- クラウド / ログイン / アカウント / 同期 / テレメトリ
- ストロークの PDF オブジェクト結合 (Ink-on-object)
- 消しゴムの部分消去 (Stroke 単位のまま)
- PDF 書き出し
- 蛍光ペンの改良
- パスワード付き PDF
- オブジェクトのマルチ選択 / グループ化
- ノートのフォルダ階層管理
- 画像オブジェクトの実装 (PdfObject と同型のため段階 5 以降に回す)

---

## 10. 関連文書

- `docs/ARCHITECTURE.md`: 現行 (PDF ビューア + Ink) のアーキテクチャ
- `docs/SPECIFICATION.md`: 現行の仕様書 (性能目標・禁止事項・入力仕様)
