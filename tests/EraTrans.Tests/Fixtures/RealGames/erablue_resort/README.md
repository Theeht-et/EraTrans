# Japanese original-game ERB fixtures

This directory contains six **complete, unmodified Japanese ERB source files** from the public `erablue_resort` game repository. They are copied as test inputs in their original directory structure; no source excerpts were cut, no code or text was rewritten, and no Chinese-localized files are used.

## Upstream project

- GitHub mirror: https://github.com/erablue-resort/erablue_resort
- Original GitGud project: https://gitgud.io/era-games/erablue_resort
- Upstream branch: `main`
- Upstream tree commit: `2b42f582df4d73359ddc66485f45d56a7cd54d30`
- Project readme: `序盤の手引き兼readme.txt`

The project's readme states under “バリアントのライセンスなど” that, except where separate licenses are provided (for example, some character dialogue/口上), modification and reuse are not restricted. The fixtures are all from core gameplay/system directories, not the separately licensed `口上` directory.

## Complete source files

| Fixture path (under `ERB/`) | Upstream blob SHA | Coverage |
|---|---|---|
| `コマンド/COMF400_移動.ERB` | `ab0282d49db17353d1b49aed9c5a99d6730aa894` | Long movement flow, encounter dialogue, prompt and INPUT choices, loops and branches |
| `シナリオ/サブイベント/サブイベント_商業区建設解禁.ERB` | `489d1baed4ce1a19aa9201a4d543cf74259e5a98` | Complete Japanese event dialogue, speaker labels, dynamic character placeholders and narrative sequence |
| `シナリオ/メインシナリオ/メインシナリオ_航路開拓計画.ERB` | `75b8cc021f902875ed56bb943fd4852cffaaeb33` | Complete main-story dialogue, formatted output, dynamic text and unlock messages |
| `一日開始時処理/起床時告白処理.ERB` | `885e1c422b4d794758bc4f4b357afdc42b1d6942` | Complete confession/proposal flows, PRINTBUTTONLC menu choices and success/hold branches |
| `起床前メニュー関連処理/料理レシピ/食堂メニュー設定処理.ERB` | `37689f019f8996385896401f6482fc4d11320ec7` | Complete HTML-based UI, menu labels, dynamic values, BINPUT and SELECTCASE |
| `探索/アイテムデータ/ITEM_薬草.ERB` | `d94a0dcdf079b057fb825522a09fd57ca3c5c58c` | Complete item description, dynamic inventory text, conditional battle feedback and formatted output |

The test copies only these six ERB files. It does not include the game's executable, assets, character dialogue packs, saves, or other game data.

## Test intent

These files exercise the extractor and semantic-context generator against full real-world Japanese source, including continuous narrative output, UI/menu choices, placeholders, dynamic strings, conditions and branches. The integration test checks the actual extracted Japanese target strings and verifies that a selected menu option receives the other option and its prompt as read-only same-screen context, while continuous event dialogue receives neighboring lines as same-group context.

All fixtures are used read-only and solely for automated tests.
