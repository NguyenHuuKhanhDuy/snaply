# UI audit (baseline)

Method: every page is rendered headlessly for Light/Dark × 1280 / 1000 / 720 px × empty / populated library (synthetic data only) and compared against the contrast checks. Regenerate everything with:

```bash
dotnet test tests/Snaply.App.Tests          # screenshots in artifacts/ui/<state>/<theme>/<size>/<page>.png and artifacts/ui/contrast.md
```

Evidence images for the issues below are in `docs/ui-audit/`. The full contrast table is `docs/ui-audit/contrast.md`.

Priority: **P0** broken layout / blocks a task, **P1** clear quality or accessibility defect, **P2** polish.
Status: `open` · `fixing` · `fixed`. Phase = task group in `openspec/changes/refresh-ui-ux/tasks.md`.

| ID | Pri | Where | Issue | Phase | Status |
|---|---|---|---|---|---|
| A-01 | P0 | Home, ≤ ~1000 px | Capture cards: title and description collide with the shortcut keycaps; "Capture full screen" is clipped to "Capture ful…" (`a01-*.png`). | 2 → 4 | **fixed** |
| A-02 | P0 | Editor, 720 px | Top bar overflows: the primary **Save** button is pushed off screen; the fixed 272 px properties panel leaves a tiny canvas (`a02-*.png`). | 2 | **fixed** |
| A-03 | P0 | Gallery with details open, 720 px | Search box is squeezed under the filter drop-downs and clipped ("Se…"); the details overlay covers most of the grid (`a03-*.png`). | 2 | **fixed** |
| A-04 | P1 | Tokens, both themes | 13 token pairs fail WCAG AA (list below). Worst: Light tertiary text 2.7–3.2:1 (dates, hints), input/button borders 1.5–1.9:1 (need 3:1), Light success/warning text 3.7–4.3:1. | 2 | **fixed** |
| A-05 | P1 | Gallery filters, Settings drop-downs | `ComboBox` uses stock Fluent look: black fill and white border in Dark, outline-only in Light. It ignores the surface/border tokens and looks different from our inputs (`a05-a06-*.png`). | 2 | **fixed** |
| A-06 | P1 | Gallery details panel | Long location path wraps over ~6 lines; file name breaks inside the extension ("…7c.p/ng"); no copy-path action. | 2 | **fixed** |
| A-07 | P1 | Home, empty library | The empty-state card never appears: `ShowEmpty` is not re-evaluated when `Loaded` flips, so the page shows only the "Recent" heading over blank space (`a07-*.png`). | 2 | **fixed** |
| A-08 | P1 | Settings | One long scrolling page (~2,900 px) with no section navigation or search. | 3 | **fixed** |
| A-09 | P2 | Settings → OCR | An empty warning box can appear when the engine reports an empty (rather than null) "unavailable" reason. | 2 | **fixed** |
| A-10 | P2 | Home | "View all" is not aligned with the tile grid's right edge; the last tile row is ragged. | 4 | **fixed** |
| A-11 | P2 | Editor right panel | Six colour swatches wrap leaving one orphan on the second row; the Tips text is small and low-contrast. | 2/6 | **fixed** |
| A-12 | P2 | Home | Disabled "Repeat last capture" renders as a heavy grey block in Light, more prominent than the enabled secondary buttons. | 2 | **fixed** |
| A-16 | P2 | Editor | The update banner is shown above the editor toolbar and pushes the editor down. | later | open |
| A-17 | P2 | Tags, 720 px | The search box next to the tag list is narrow ("Search by file…" truncated) though still usable. | 4 | open |
| A-13 | P2 | Toasts | Four stacked toasts cover the content they refer to; no max height / grouping when many arrive. | 2 | deferred |
| A-14 | P2 | Gallery | No bulk actions, grouping or list view (planned feature work, not a defect). | 5 | fixed: grid/list toggle, day groups, Ctrl/Shift/checkbox multi-select, bulk favorite/tag/export/delete, keyboard navigation (`after-phase5-*` in docs/ui-audit) |
| A-15 | P2 | Editor vs capture overlay | Same tools, different layout/order/shortcuts in the two surfaces. | 6 | fixed: one `AnnotationToolset` (order, icons, tooltips, shortcut keys, palette, strokes) drives both; overlay gained letter shortcuts |

## Fixes verified

After images: `docs/ui-audit/after-*.png`. Phase 2 changes: Home capture cards (shortcuts under the text), editor icon-only toolbar and slimmer panel below 880 px, gallery details panel floats over the grid below 880 px, tag list narrows below 880 px, themed `ComboBox`, one-line file name and path with tooltip and Copy path, one-row colour swatches, quiet disabled buttons, focus-visible rings, Home empty state, and token changes (new `ControlBorder` token; darker tertiary text and status colours in Light, lighter tertiary text in Dark). `ContrastTests` now has no known failures and `AccessibilityTests` checks every interactive control for an accessible name on every page.

## A-04 contrast pairs that failed at baseline (all fixed)

| Theme | Foreground → Background | Ratio | Needs |
|---|---|---|---|
| Light | TextTertiary → Background | 2.95 | 4.5 |
| Light | TextTertiary → Surface | 3.16 | 4.5 |
| Light | TextTertiary → Sidebar | 2.74 | 4.5 |
| Light | Error → ErrorSoft | 4.28 | 4.5 |
| Light | Success → Surface | 4.33 | 4.5 |
| Light | Success → SuccessSoft | 3.75 | 4.5 |
| Light | Warning → Surface | 4.20 | 4.5 |
| Light | Warning → WarningSoft | 3.69 | 4.5 |
| Light | BorderStrong → Surface | 1.66 | 3.0 |
| Light | BorderStrong → Background | 1.55 | 3.0 |
| Dark | TextTertiary → Surface | 4.23 | 4.5 |
| Dark | BorderStrong → Surface | 1.72 | 3.0 |
| Dark | BorderStrong → Background | 1.95 | 3.0 |

`tests/Snaply.App.Tests/ContrastTests.cs` lists these as known failures. New failures break the test, and the list must shrink as A-04 is fixed.

## Not checked yet (needs a real desktop or later phases)

- Keyboard focus order and visible focus rings (A11y task 2.2); screen-reader names.
- Real-DPI rendering, animations/reduced motion, tray menu, capture overlay visuals (rendered only on a live desktop).
- macOS and Linux appearance.

## Observations that are fine

- Empty gallery state and Tags page layouts hold up in both themes; dialog and toast styling is consistent and readable; Light and Dark share the same structure.

## Final verification (refresh-ui-ux)

- Full automated run: 188 tests green (Editor 25, OCR 13, Core 71, Infrastructure 43, App 31 incl. screenshot renders for every page, both themes, three widths).
- Not done: manual pass on a real desktop for this refresh, and the optional pixel magnifier / colour readout on the capture overlay (deferred; no audit item depends on it).
- Still open (P2): A-13 toasts, A-16 banner above editor, A-17 Tags search width.

