# Real-game ERB fixtures

This folder contains curated excerpts, not complete game files. They are used as parser/grouping integration samples; game assets and unrelated data are not included.

## Upstream source and attribution

- Game: eraNAS, a substantial eraToho The World overhaul.
- Upstream project: https://gitgud.io/mrpopsalot/pops-tw
- Upstream license: GNU Lesser General Public License v3.0 or later (LGPL-3.0-or-later). The upstream project tree records the relicensing of eraNAS and its original dialogue under LGPL 3 or later. A copy of the license is included as LICENSE.
- Upstream license file: https://gitgud.io/mrpopsalot/pops-tw/-/blob/dev/omogatari-kai/LICENSE
- Upstream README: https://gitgud.io/mrpopsalot/pops-tw/-/blob/dev/omogatari-kai/README.MD

## Source excerpts used

| Fixture | Upstream path | Source revision |
|---|---|---|
| COM/ADD_CUSTOM_COM478 Building.ERB | ERB/TRANSLATION/OMOGATARI/COM/ADD_CUSTOM_COM478 Building.ERB | e2f0c2c0c104ecfbe0a07ad804a0c2db3d7305e5 |
| COM/COMF447 Set Up A Stall.ERB | ERB/TRANSLATION/OMOGATARI/COM/COMF447 Set Up A Stall.ERB | 5b48a05fc10dc461d580fc39db1b4029a04ee3a3 |
| COMBAT/KNIFE_FIGHT.ERB | ERB/TRANSLATION/OMOGATARI/COMBAT/KNIFE_FIGHT.ERB | 5b48a05fc10dc461d580fc39db1b4029a04ee3a3 |
| Factions/FactionStuff.ERB | ERB/TRANSLATION/OMOGATARI/Factions/FactionStuff.ERB | 5b48a05fc10dc461d580fc39db1b4029a04ee3a3 |
| Factions/FactionWaring.ERB | ERB/TRANSLATION/OMOGATARI/Factions/FactionWaring.ERB | 5b48a05fc10dc461d580fc39db1b4029a04ee3a3 |
| ITEM/Item Modding.ERB | ERB/TRANSLATION/OMOGATARI/ITEM/Item Modding.ERB | e2f0c2c0c104ecfbe0a07ad804a0c2db3d7305e5 |

The excerpts cover menu/output commands, conditional branches, function calls, formatted dynamic text, loops, GOTO, variables, and DataTable calls. They have been trimmed to keep the test corpus small and focused. Each fixture is marked as an excerpt in its leading comments.

EraTrans uses these files only as read-only test inputs. The samples are not redistributed as a playable game or as a replacement for the upstream project.