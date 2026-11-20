# EraTrans

A lightweight Windows desktop tool for translating ERA games.

## Features

- Extract and edit JSON dictionaries in a folder tree while preserving legacy ERA keys and unknown fields.
- Store generated files in a subfolder named after the original game directory, keeping each game's dictionaries, AI context, and working copy separate.
- Keep the original game directory read-only; operations first make a complete game copy, then modify only the copy.
- Show source context for the selected dictionary entry. Re-extraction can backfill missing context fields without changing existing translations or custom dictionary fields.
- Translate selected or pending dictionary entries through OpenAI-compatible Chat Completions APIs.
- Optionally enable ERB semantic-group translation. This keeps the legacy dictionary JSON format unchanged and stores source grouping metadata in separate files under `AI上下文/`.
- Configure multiple providers with a custom Base URL, model name, and API key.
- Encrypt saved API keys with Windows DPAPI for the current Windows user.
- Keep AI output in the editor for review; dictionary files are only changed after the user saves.
- Apply translations to a complete game working copy, including resources and files that do not need translation. The original game directory is not modified.
- Configure parser options in the settings window, including encoding, variable filtering, duplicate merging, context output, auto-correction, extension matching, variable-name characters, and expression operators. Variable dictionary entries are suppressed by default and can be re-enabled in settings.

## Portable configuration

All settings are stored beside `EraTrans.App.exe`, in the `Config/` folder:

- `Config/settings.json` — parser, extraction, output, and AI translation mode settings.
- `Config/ai-providers.json` — AI provider profiles. API keys remain encrypted with Windows DPAPI (CurrentUser), not stored as plaintext.

The software folder must be writable by the current Windows user. For a portable setup, extract the downloaded artifact into a normal user-writable folder rather than `Program Files`.

The parser settings window provides options for file encoding, variable-name filtering, automatic variable correction, hiding English entries, merging duplicate entries, hiding variable entries, merging concatenated strings, writing context, target file extensions, reference dictionaries, variable-name characters, and expression operators. Variable dictionary entries are hidden by default; this can be changed in settings. Advanced lists accept one item per line.

## AI provider configuration

Configure a provider in the dictionary editor. The Base URL should point to the API's OpenAI-compatible base endpoint, usually ending in /v1, rather than a full /chat/completions path. The client uses the official OpenAI .NET SDK, which supports custom endpoints.

The initial integration supports the OpenAI-compatible Chat Completions protocol. It does not yet implement separate native Claude, Gemini, or Responses API protocols.

AI translation only targets editable, non-variable entries whose stage is 0, whose original text is non-empty, and whose translation is empty. AI results are mapped by dictionary entry index and are not automatically saved or marked as reviewed (stage remains unchanged). Existing translations and ignored entries are never overwritten.

ERB semantic-group translation is disabled by default. When enabled in 设置, ERB dictionaries use the separate `AI上下文/` sidecar files to provide function, adjacent-output, menu, and conditional-branch context. Text from alternative branches and other group entries is reference-only; the model is still asked to translate only the explicitly selected target entries. Non-ERB dictionaries continue using the legacy method. A missing, stale, or invalid sidecar causes the affected ERB dictionary to fall back to the legacy method. Re-extract a dictionary after changing its source file or its keys/original text; changing translations alone does not invalidate the grouping metadata.

## Download a Windows build

Each successful GitHub Actions CI run publishes one `EraTrans-win-x64` artifact. Downloading the artifact already gives you a ZIP archive; extract it once to get the application files directly. There is no second ZIP inside.

1. Open the repository's Actions tab.
2. Open the successful CI run for the commit or pull request you want.
3. Download `EraTrans-win-x64` from the Artifacts section.
4. Extract the downloaded artifact ZIP once and run `EraTrans.App.exe`.

The package uses .NET single-file publishing and includes the .NET runtime, so a separate .NET installation is not required. The EXE is the only file in the package root. Configuration is grouped under `Config/`, and user documentation is under `Docs/`. Keep those folders beside the EXE. CI artifacts are retained for 30 days.

## Output directory layout

Choose one original game directory and one unified output directory in the app. EraTrans uses the final folder name of the original game directory to create an isolated output area:

```text
统一输出目录/
└─ 原游戏文件夹名称/
   ├─ 字典/
   ├─ AI上下文/
   └─ 游戏副本/
```

- `字典/` — extracted JSON dictionaries and user-edited translations. The existing JSON dictionary structure and keys are unchanged.
- `AI上下文/` — separate ERB semantic-group metadata files generated during extraction and read only when semantic-group translation is enabled.
- `游戏副本/` — a complete copy of the original game directory, including unrelated assets and empty folders. Existing copies are never overwritten.

The main window previews these paths after both directories are selected. The original game directory remains untouched.

To rebuild a clean translated copy after changing translations, preserve any files you want to keep and remove or move the current game's `游戏副本/` folder before applying translations again.

## Technology

- C# / WPF
- .NET 10
- OpenAI .NET SDK
- GitHub Actions for build, test, and Windows packaging
